// Based on this article:
// http://www.gabescode.com/dotnet/2018/11/01/basic-HttpListener-web-service.html
// Additional features and overall implementation should be noted as additions;
// this project isn't just a direct copy or 1-for-1 match of the article.

using UnityEngine;
using System;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.NetworkInformation;

/// <summary>
/// Custom enum to help specify how we're setting the server's port.
/// </summary>
public enum PortChooseMethod
{
    DefaultOrError,
    DefaultOrIncrement,
    WithinRange,
    Random
}

public class EmbeddedRestAPI : MonoBehaviour
{
    /// <summary>
    /// Singleton to access the game's REST API. Practically, you should only have one server running per executable to avoid developer confusion and networking issues.
    /// </summary>
    public static EmbeddedRestAPI Instance;

    /// <summary>
    /// Array of IP addresses that can represent this device within the networks that it's connected to.
    /// </summary>
    [Tooltip("For LAN or WLAN networks; the addresses that this device is connected to. There may be multiple addresses, as a device can be connected to multiple networks at once.")]
    public string[] internalIPs;

    /// <summary>
    /// The method the server will use to determine its port on server startup.
    /// </summary>
    [Tooltip("Controls how the API server's port will be chosen.")]
    public PortChooseMethod portChooseMethod = PortChooseMethod.WithinRange;

    /// <summary>
    /// The range of ports you want the server to pick a port from. Ports in use by other applications will be ignored by the server's port-choosing at server startup, so this array should cover a safe variety of ports. This array should absolutely be shared in your game documentation / modding documentation so that other apps know which ports to communicate with.
    /// This array will be used by the "WithinRange" port-choosing method to determine an available port.
    /// </summary>
    [Tooltip("The pool of possible ports that the API server may use, if the port-choosing method is WithinRange.")]
    public int[] portRange;

    /// <summary>
    /// If the "PortChooseMethod.DefaultOrIncrement" is used, this determines how many increments on the default port will be attempted before giving up. A single device has a lot of ports - if you're not sure what you should set this to, leave it as-is.
    /// </summary>
    [Tooltip("How many additional ports should be tried if the port-choosing method is DefaultOrIncrement and the default port is unavailable.")]
    public int incrementRangeLimit = 1000;

    /// <summary>
    /// This is the port used by the server when the "StrictOne" port-choosing method is selected.
    /// </summary>
    [Tooltip("The port used if a port selection method fails, or if the port-choosing method is left to a default-using method.")]
    public int defaultPort = 43000;

    /// <summary>
    /// Port for the API to use. Should be overridden at runtime, but defaults are nice.
    /// </summary>
    [Tooltip("The port currently used by the API server.")]
    public int serverPort = 43000;

    /// <summary>
    /// HttpListener instance for the API. API lives as long as this instance does.
    /// </summary>
    private HttpListener restApi = new HttpListener();

    /// <summary>
    /// Flag for the API to know whether it should be running or not. Can be affected by multiple threads simultaneously since it's volatile!
    /// </summary>
    [Tooltip("A flag to control whether or not the server should be active & listening for traffic right now.")]
    public volatile bool _keepRunning = true;

    /// <summary>
    /// A reference to the API's lifecycle loop.
    /// </summary>
    public Task _mainServerLoop;

    /// <summary>
    /// Start the API instance if it isn't running already, and update its associated reference.
    /// </summary>
    public void StartWebServer()
    {
        // Server already running, don't run another one.
        if (_mainServerLoop != null && !_mainServerLoop.IsCompleted) return;

        // Start the server and store the variable that keeps a reference to it.
        _mainServerLoop = MainLoop(); 
    }

    /// <summary>
    /// Stop the API instance, interrupting any in-progress requests.
    /// </summary>
    public void StopWebServer()
    {
        // Change this to false ASAP to impact the main server loop and make it stop accepting new requests
        _keepRunning = false;

        // Bluntly stop the server, killing any in-progress requests.
        restApi.Stop();

    }

    /// <summary>
    /// Process any web traffic directed to the API instance and handle necessary responses.
    /// </summary>
    /// <returns></returns>
    private async Task MainLoop()
    {
        // Initialize the API server.
        try
        {
            // Determine the server's port based on a selected choosing method.
            switch (portChooseMethod)
            {
                case PortChooseMethod.DefaultOrError:
                    serverPort = defaultPort;
                    break;
                case PortChooseMethod.WithinRange:
                    serverPort = GetPortWithinRange(portRange);
                    break;
                case PortChooseMethod.Random:
                    serverPort = GetAvailablePort();
                    break;
                case PortChooseMethod.DefaultOrIncrement:
                    goto default;
                default:
                    serverPort = GetPortWithinIncrementRange(defaultPort, incrementRangeLimit);
                    break;
            }

            // Configure the addresses that the server will listen to, with localhost always included.
            List<string> listenerPrefixes = new List<string>();
            listenerPrefixes.Add($"http://localhost:{serverPort}/");

            // Determine the local network addresses (in addition to localhost) that the server will listen to.
            internalIPs = GetInternalNetworkAddresses();
            foreach (string address in internalIPs)
            {
                listenerPrefixes.Add($"http://{address}:{serverPort}/");
            }

            // Create the server instance and configure it with the addresses to listen to.
            restApi = new HttpListener();
            foreach (string address in listenerPrefixes)
            {
                restApi.Prefixes.Add(address);
            }
            restApi.Start();
            Debug.Log($"Starting game API server on localhost:{serverPort}/");
        }
        catch (Exception error)
        {
            Debug.LogError($"Game API server didn't start. Error:\n{error}");
            _keepRunning = false;
        }

        // This is the "keep an eye on web traffic" part, constantly watching for incoming requests.
        while (_keepRunning)
        {
            try
            {
                // GetContextAsync() returns when a new request come in
                var context = await restApi.GetContextAsync();
                // Make sure whatever thread we're on isn't fighting another thread to access this particular request.
                lock (restApi)
                {
                    // Hand the request off to our processor function.
                    // Note that we're still checking "_keepRunning" even though the containing loop is checking it;
                    // another thread may have changed the flag while we locked the web request to this thread,
                    // so we need to check again that the server is still operating before actually moving on.
                    if (_keepRunning) ProcessRequest(context);
                }
            }
            catch (Exception error)
            {
                // HttpListenerException gets thrown when the listener is stopped,
                // will happen as per regular operations.
                // How does this "error is SpecificErrorException" part work? Polymorphism!
                // All SpecificErrorException classes should be inheriting from the Exception class,
                // allowing polymorphic code like this to work.
                if (error is HttpListenerException) return;

                // If we're okay with the possibility that some in-progress network requests might get gutted
                // before they finish if the server closes, then we can also do this...
                if (error is ObjectDisposedException) return;

                // If it wasn't a HttpListenerException, an actual error occured and that needs to be logged:
                Debug.LogError($"Game API server encountered an error. Error:\n{error.Message}");

            }
        }
    }

    private void ProcessRequest(HttpListenerContext context)
    {
        using (HttpListenerResponse response = context.Response)
        {
            // Both "try" and "catch" end up needing to write some data to a byte buffer,
            // but it's the only property that is cleanly shared between those two blocks.
            byte[] buffer;

            try
            {
                // Help establish data for our control flow and initialize the server response content.
                bool handled = false;
                string responseBody = "";


                // On larger routes like "localhost:port/commands/settimeofday",
                // get the "top"-level router by substringing the absolute path.
                // AbsolutePath begins with a forward slash,
                // then our top-level route ends with a second forward slash.
                // So, a bit of cleaning is required on the path while we work with it.

                // Remove leading forward slash
                string highLevelRoute = context.Request.Url.AbsolutePath.Substring(1);

                Debug.Log($"Absolute URL path for a game API server request is {context.Request.Url.AbsolutePath}");
                Debug.Log($"Received a Game API request at {highLevelRoute}");

                // Determine if additional forward slashes are in the path
                if (highLevelRoute.IndexOf("/") >= 0)
                {
                    // Find out what the "router" in the path is by identifying the string before the forward slash
                    string routerName = highLevelRoute.Remove(highLevelRoute.IndexOf("/"));
                    Debug.Log($"Splitting the request path into top-level routing, got {routerName}");

                    // Use the router name in the router switch-case,
                    // but still pass the full highLevelRoute value along for the router to use
                    // (eg. multiple levels of forward slashes such as "localhost:port/commands/settimeofday/0800")
                    switch (routerName)
                    {
                        case "commands":
                            // TODO: Add example of functionality with multiple levels of forward slashes.
                            responseBody = $"Route with multiple forward slashes detected! It was {context.Request.Url.AbsolutePath}";
                            break;
                    }

                }
                else
                {
                    // Top-level route has no forward slashes, easy to work with.

                    switch (highLevelRoute)
                    {
                        // This is where we do different things depending on the URL
                        // This would cover "localhost:port/status"
                        case "status":

                            // Whatever the route is meant to send back should be a JSON string
                            // so do whatever you need in other functions and return that JSON string to 
                            // this responseBody
                            responseBody = GameInfoRouter.GameStatus(context);

                            break;
                        case "ping":
                            responseBody = GameInfoRouter.RequestMirror(context);
                            break;
                        case "score":
                            responseBody = GameInfoRouter.GameScore(context);
                            break;
                        case "address":
                            responseBody = RestServerInfoRouter.ServerAddress(context);
                            break;
                    }
                }


                // If responseBody contains some content to return, return it
                if (!String.IsNullOrEmpty(responseBody) && !String.IsNullOrWhiteSpace(responseBody))
                {
                    // Convert the responseBody into a byte array.
                    buffer = Encoding.UTF8.GetBytes(responseBody);

                    // Configure the settings of the response.
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    response.ContentLength64 = buffer.Length;

                    // Writing to the OutputStream is what actually sends a response back to the client.
                    // Make sure all configuration of the response is done before you do this!
                    response.OutputStream.Write(buffer, 0, buffer.Length);

                    // Flag this request as handled - it's all done!
                    handled = true;

                }

                // If responseBody is empty, return a routing error.
                if (!handled)
                {
                    // Browsers know that 404 means the requested route was invalid.
                    // We don't need to do much within this app about that.
                    response.StatusCode = 404;
                }
            }
            catch (Exception error)
            {
                // Convert the error into a byte array.
                buffer = Encoding.UTF8.GetBytes(JsonUtility.ToJson(error));

                // Configure the settings of the response.
                // In large-scale, fully-produced apps you could easily make your own exceptions
                // with their own instructions on how to configure this response (eg. specific StatusCode values).
                // TODO: Example of that, lol.
                response.StatusCode = 500;
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;

                // Writing to the OutputStream is what actually sends a response back to the client.
                // Make sure all configuration of the response is done before you do this!
                response.OutputStream.Write(buffer, 0, buffer.Length);

                // Within the app, we can log more data that may help us developers process the issue.
                // Don't need to show all developer info to the end-user!
                Debug.LogError($"The server encounted an error while processing a request. Some details:\nAttempted API path: {context.Request.Url.AbsolutePath}\nError: {error.Message}");
            }
        }
    }

    #region Unity app lifecycle methods

    private void Awake()
    {
        // Set up the singleton for this server instance.
        if (Instance == null)
        {
            Instance = this;
            
        } else
        {
            Destroy(this);
        }
    }

    private void Start()
    {
        Debug.Log("Starting the Embedded API server...");
        StartWebServer();
    }

    private void OnDestroy()
    {
        Debug.Log("Stopping the Embedded REST API server...");
        StopWebServer();
    }
    #endregion


    /// <summary>
    /// Identifies an available port on the host machine by creating a TcpListener and copying its port number. The TcpListener is disposed when the function returns, so the port is guaranteed to be free if used immediately.
    /// </summary>
    /// <returns>The available port number identified by the function. Highly likely to return a different port number on every execution of this function.</returns>
    public static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    // Why don't we just use TcpListener the whole time?
    // Because TcpListener needs a whole bunch of work done to enable simple REST API stuff.
    // HttpListener has that work done for us already so it's great for REST APIs,
    // but it can't find an unused port like TcpListener can.

    /// <summary>
    /// Figures out which ports are available and not already-bound on this machine, from a provided array of ports.
    /// </summary>
    /// <param name="portRange">The array of ports that you desire your server to listen to. Only one of these ports will be chosen though.</param>
    /// <returns>The first-available port from the provided port range.</returns>
    public static int GetPortWithinRange(int[] portRange)
    {
        int firstAvailablePort = 0;
        IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
        IPEndPoint[] tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();

        foreach (int port in portRange)
        {
            bool portInUse = false;
            foreach (IPEndPoint endPoint in tcpConnInfoArray)
            {
                if (endPoint.Port == port)
                {
                    // It only takes one match to say "this port is already bound, it can't be used".
                    portInUse = true;
                    // There can be tens of thousands of ports in use, iterating through them all when you quickly discover a port in use is a waste of time - so break ASAP.
                    break;
                }
            }
            if (!portInUse)
            {
                // Break out of the foreach, we found an available port!
                firstAvailablePort = port;
                break;
            }
        }

        return firstAvailablePort;
    }

    /// <summary>
    /// Figures out which ports are available within a range, based on an incrementing list of wanted ports. For example, if you call this function with a startingPort of 3000 and rangeSize of 5, possible returns could be 3000, 3001, 3002, 3003, 3004. The first available port from that range will be returned.
    /// </summary>
    /// <param name="startingPort">The port that you want the range to start from.</param>
    /// <param name="rangeSize">How many incrementations of the startingPort should be checked.</param>
    /// <returns>An available port within the specified rangeSize and based on the startingPort.</returns>
    public static int GetPortWithinIncrementRange(int startingPort, int rangeSize)
    {
        int[] portIncrementRange = new int[rangeSize];
        for (int i = 0; i < rangeSize; i++)
        {
            portIncrementRange[i] = startingPort + i;
        }
        return GetPortWithinRange(portIncrementRange);
    }


    /// <summary>
    /// Returns an array of all IP addresses that the device running this application is represented by within its connected networks. This is not a list of public IPs, but the 192.*.*.* or 172.*.*.* addresses used in LANs and WLANs.
    /// </summary>
    /// <returns>Array of IP addresses as strings.</returns>
    public string[] GetInternalNetworkAddresses()
    {
        List<string> addressesToReturn = new List<string>();
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                addressesToReturn.Add(ip.ToString());
            }
        }

        return addressesToReturn.ToArray();
    }

}