using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using System;
using System.IO;

public class GameInfoRouter
{
    public static string GameStatus(HttpListenerContext context)
    {
        string responseBody = "";
        switch (context.Request.HttpMethod)
        {
            case "GET":

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

    public static string RequestMirror(HttpListenerContext context)
    {
        string responseBody = "";
        switch (context.Request.HttpMethod)
        {
            case "POST":

                string receivedData = "";

                using (StreamReader dataReader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    receivedData = dataReader.ReadToEnd();
                }

                // Data looks nicest when POST request sends in raw JSON data.
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
            case "GET":

                responseBody = JsonUtility.ToJson(new PlayerScore
                {
                    score = DummyDataManager.Instance.playerScore
                });

                break;
            case "POST":
                string receivedData = "";

                using (StreamReader dataReader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    receivedData = dataReader.ReadToEnd();
                }
                Debug.Log($"Incoming score data looks like: {receivedData}");
                PlayerScore newPlayerScore = JsonUtility.FromJson<PlayerScore>(receivedData);


                DummyDataManager.Instance.playerScore = newPlayerScore.score;

                responseBody = $"Score updated to {DummyDataManager.Instance.playerScore}";
                break;
        }

        return responseBody;
    }
}
