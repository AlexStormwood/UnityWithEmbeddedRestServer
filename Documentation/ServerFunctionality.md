# Server Functionality
There are a few different aspects impacting how the API server works, and this page will break down some of the larger or more-important sections into informative chunks for you.

## Server boot-up and lifecycle
For the sake of this project, the API server will automatically start and shutdown based on the Unity lifecycle of the SampleScene. When the SampleScene plays, the server starts. When the SampleScene unloads or stops playing, the server stops.

But that's a simplification - you can absolutely control the server's start-up and shutdown yourself.

This is the code that this project uses to startup and shutdown the server, as seen in the EmbeddedRestAPI script:

```C#
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


```

As you can see, there are methods specifically for starting and stopping the web server. If you want the API server to start or stop on your own specific requirements or actions, just change when those methods are called.

Also to note: this project began in a DOTS-using Unity project and depended on some other things being set up within the Awake stage of a scene. That's carried over to here for no good reason, so the server starts in the Start stage of a scene. You can absolutely start the server during the Awake stage in your own project.

When `StartWebServer()` is executed, it does two things:
1. Check if the server is already running. This server is multithreaded, so it'd be more-nightmarish than regular code if we end up with multiple instances of the server running and trampling on eachother. 
2. Start the server _and_ store that running server as a reference, in case anything else needs to do something with the server.

```C#

    public void StartWebServer()
    {
        // Server already running, don't run another one.
        if (_mainServerLoop != null && !_mainServerLoop.IsCompleted) return;

        // Start the server and store the variable that keeps a reference to it.
        _mainServerLoop = MainLoop(); 
    }

```

The MainLoop task (task = run on other CPU threads!) has two main components - and it'll spend a lot of time within the second component.

```C#

    private async Task MainLoop()
    {
        // Initialize the API server.
        try{}catch{}

        // This is the "keep an eye on web traffic" part, constantly watching for incoming requests.
        while (_keepRunning){try{}catch{}}
    }

```

On server start, the MainLoop task runs once - it'll keep running due to its `while` loop, but the initialization will also happen (just once) prior to entering that loop. The initialization will be covered below, and the "while server is running" behaviour will be covered by the "Server running" heading further below.

```C#

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
        while (_keepRunning){
        // Covered in "Server running" below
        }
    }

```

A lot of the initialization is dedicated to determining what addresses and ports should actually be used by the API server. Once server is configured with those values, it's started.


## Server running
The server runs within a `while` loop - not exactly glamourous.

```C#

    private async Task MainLoop()
    {
        // Initialize the API server.
        try{
        // Covered above already.
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

```

The code above is a lot of "if the client network request is an error, do XYZ, otherwise pass it along to be processed in another function".

There's supposedly a nicer way to handle the errors, especially as Unity triggers an error in the server on shutdown. But, this works just fine for standard API operations.

So, what is the mystical `ProcessRequest()` function that handles the client's network request? Let's have a look:

```C#


    private void ProcessRequest(HttpListenerContext context)
    {
        using (HttpListenerResponse response = context.Response)
        {
            // Creating the byte[] buffer before the try-catch begins.
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


```

It's a lot, and it's not even the end of the process! 

Basically, this script is parsing the requested URL. What is the client trying to access within this API? 

Then, once we know what the client is trying to access, we pass their request along to a dedicated class to handle that request. 

> More passing-things-along, yes - but doing a web server with a basic HttpListener is never going to be as simple as something like ExpressJS or Ruby on Rails or even Grapevine. 
> 
> By sticking to a low-level HTTP client such as the HttpListener, we guarantee compatibility across more platforms & devices. After all, every Unity executable can be a web server this way - but a Nintendo Switch build might not support the same fancy web server libraries or frameworks as a powerful Windows x64 build!

So, what happens when we pass along the client's request to the "router" class of an associated path? This code will get the routing started:

```C#

                        case "status":

                            // Whatever the route is meant to send back should be a JSON string
                            // so do whatever you need in other functions and return that JSON string to 
                            // this responseBody
                            responseBody = GameInfoRouter.GameStatus(context);

                            break;

```

But that literally passes the `context` (the client's web request) to another class. Let's dig into that. 

These other classes are "routers", unofficially. Basically we used the previous steps to determine the first stage of routing - but if you've got multiple chunks of functionality that are similar, or at a similar scope of the project, or affect similar things within the game, maybe you'd need an extra step of routing. Here's the basic structure of a "router" class in this project:

```C#

public class GameInfoRouter
{
    public static string GameStatus(HttpListenerContext context){}

    public static string RequestMirror(HttpListenerContext context){}

    public static string GameScore(HttpListenerContext context){}
}

```

As you can see, the router just contains a bunch of functions that return strings. These are JSON strings, so we can keep data consistently-formatted between applications (eg. web browser <-> game executable communication).

Let's focus on the GameStatus router first.

```C#

    public static string GameStatus(HttpListenerContext context)
    {
        // Guarantee that at least something will be returned, even if it's an empty string.
        // Other code will handle "omg no response to send!" situations.
        string responseBody = "";

        // Determine what HTTP verb we're working with.
        // This is basically the second level of routing - what exactly are we doing in this router?
        switch (context.Request.HttpMethod)
        {
            case "GET":

                // Using Unity's built-in JSON utility class,
                // we can create a response JSON string that has actual data.
                // However, each JSON string relies on an object structure - so a struct or class must exist
                // to represent each possible API response.
                responseBody = JsonUtility.ToJson(new GameHealthCheck
                {
                    fps = (1 / Time.deltaTime),
                    platform = Application.platform.ToString(),
                    sessionLengthSeconds = Time.realtimeSinceStartup
                });

                break;

        }

        return responseBody;
    }

```


But what if we have more-complex requests? What if we want to send in data to our API and influence the game via the API? We can do that too.

```C#

    public static string GameScore(HttpListenerContext context)
    {
        string responseBody = "";
        switch (context.Request.HttpMethod)
        {
            // Nice and simple GET: Just get the current game score and package that up for the response.
            // Again, this response needs another structured object;
            // so PlayerScore is yet another class we need to make.
            case "GET":

                responseBody = JsonUtility.ToJson(new PlayerScore
                {
                    score = DummyDataManager.Instance.playerScore
                });

                break;

            
            // Since HTTP verbs are just strings,
            // any verbs we want to ignore can simply be left out of the switch case.
            case "POST":
                string receivedData = "";

                // We must use a StreamReader as that works best with the HttpListenerContext's InputStream structure.
                // Once the data has been read, the reader is disposed via the using(){} syntax.
                using (StreamReader dataReader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    receivedData = dataReader.ReadToEnd();
                }
                Debug.Log($"Incoming score data looks like: {receivedData}");

                // In a professional, customer-facing product, you'd want a lot of sanitization and authorization
                // and various other checks in place to make sure that this raw data doesn't break the game.
                // But other than that, once you use the Unity JSON utility to parse the received data,
                // you can do whatever you want with it!
                PlayerScore newPlayerScore = JsonUtility.FromJson<PlayerScore>(receivedData);
                DummyDataManager.Instance.playerScore = newPlayerScore.score;

                // In this project, we're assuming all went well. 
                // We can just send a nice message back to the client to say
                // that the request is done & applied to the game.
                responseBody = $"Score updated to {DummyDataManager.Instance.playerScore}";
                break;
        }

        return responseBody;
    }

```

For the sake of this little example project, we don't have anywhere near as much security or checks in the POST request. You should have some form of authentication, authorization, sanitization and other processes in place to make sure that the web request's data isn't about to crash your game if you apply it to the game.

But for this project, we're staying simple: 
1. The web request contains a JSON string.
2. We receive that string and deserialize it into an object.
3. We transfer data from that object to various relevant systems in the game.
4. We send the client a message saying "request worked, data applied!" and wrap up the response.


## JSON is the magic language

APIs are kinda nice and "simple". Simple in quotes.

To get data from one application to another, you can use JSON.

Every programming language has a way to translate JSON data into that-specific-language data.

Once you've got data from the web request into your specific language's data structure, you just work with that structured data the same way you'd work with anything else in your game.

```C#

                string receivedData = "";

				// Receive new data from the web request:
                using (StreamReader dataReader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    receivedData = dataReader.ReadToEnd();
                }

                PlayerScore newPlayerScore = JsonUtility.FromJson<PlayerScore>(receivedData);

				// Apply the new data to the existing game:
                DummyDataManager.Instance.playerScore = newPlayerScore.score;


```

If you need to send data back to the requesting client, you need to convert your programming language's data structure into JSON data and send that back.

```C#

				// Convert a class or struct into a JSON string:
                responseBody = JsonUtility.ToJson(new GameHealthCheck
                {
                    fps = (1 / Time.deltaTime),
                    platform = Application.platform.ToString(),
                    sessionLengthSeconds = Time.realtimeSinceStartup
                });
                // Send that string back as the response.

```

That's magical, cross-language, cross-application communication right there.

It's what makes an API so useful and powerful!