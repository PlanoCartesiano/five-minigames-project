using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PauseMenu : MonoBehaviour
{
    public static bool isPaused;
    public GameObject pauseMenu;
    void Start()
    {
        pauseMenu.SetActive(false);
    }

    void Update()
    {
        CheckPause();
    }

    #region Pause
    public void PauseGame()
    {
        isPaused = true;
        pauseMenu.SetActive(true);
        Time.timeScale = 0f;
    }

    public void ResumeGame()
    {
        isPaused = false;
        pauseMenu.SetActive(false);
        Time.timeScale = 1f;
    }

    private void CheckPause()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (isPaused)
                ResumeGame();
            else
                PauseGame();
        }
    }
    #endregion

    #region Buttons
    public void OnQuitToMenuClick()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene("StartSceneSkyscraper");
    }
    #endregion
}

