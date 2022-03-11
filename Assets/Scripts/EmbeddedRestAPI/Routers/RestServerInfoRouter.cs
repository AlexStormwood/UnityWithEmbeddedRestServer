using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using System;
using System.IO;

public class RestServerInfoRouter
{
    public static string ServerAddress(HttpListenerContext context)
    {
        string responseBody = "";
        switch (context.Request.HttpMethod)
        {
            case "GET":

                responseBody = JsonUtility.ToJson(new ServerInfo
                {
                    privateAddresses = EmbeddedRestAPI.Instance.GetInternalNetworkAddresses()
                });

                break;

        }

        return responseBody;
    }

    
}
