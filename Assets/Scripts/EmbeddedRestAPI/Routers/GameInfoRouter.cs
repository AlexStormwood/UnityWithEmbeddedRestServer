using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using System;
using System.IO;

/// <summary>
/// Router for overall, general game-related functionality with the game API server.
/// </summary>
public class GameInfoRouter
{
    /// <summary>
    /// Get some key information about the currently-running game.
    /// </summary>
    /// <param name="context">The HttpListenerContext object that represents the client's web request.</param>
    /// <returns>JSON string with key pieces of information about the game.</returns>
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

    /// <summary>
    /// This simply copies whatever data was attached to the web request and sends it back to the client.
    /// Essentially, this is a ping-pong route.
    /// </summary>
    /// <param name="context">The HttpListenerContext object that represents the client's web request.</param>
    /// <returns>A JSON string with data matching what the client sent in.</returns>
    public static string RequestMirror(HttpListenerContext context)
    {
        string responseBody = "";
        switch (context.Request.HttpMethod)
        {
            case "POST":

                string receivedData = "";

                // We must use a StreamReader as that works best with the HttpListenerContext's InputStream structure.
                // Once the data has been read, the reader is disposed via the using(){} syntax.
                using (StreamReader dataReader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    receivedData = dataReader.ReadToEnd();
                }

                // This log looks nicest when POST request sends in raw JSON data.
                Debug.Log($"Received this data on the RequestMirror route: {receivedData}");

                responseBody = receivedData;

                break;

        }

        return responseBody;
    }

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
}
