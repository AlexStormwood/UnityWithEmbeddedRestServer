using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DummyDataManager : MonoBehaviour
{
    public static DummyDataManager Instance;


    public int playerScore;
    public Vector3 playerPosition;


    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this.gameObject);
        }
        else
        {
            Instance = this;

            DontDestroyOnLoad(this.gameObject);
        }
    }

}
