using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ExecuteGame : MonoBehaviour
{
    public string sceneToLoad;

    public void OpenApp()
    {
        if (!string.IsNullOrEmpty(sceneToLoad))
        {
            Debug.Log("Executando Skyscraper");
            SceneManager.LoadScene(sceneToLoad);
        }
        else
        {
            Debug.Log("Abrindo app simulado...");
        }
    }
}
