using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class DistanceCounter : MonoBehaviour
{
    [Header("Singleton Instance")]
    public static DistanceCounter instance;

    [Header("UI Reference")]
    public TextMeshProUGUI distanceText;

    [Header("Settings")]
    private float speed = 6f;

    public float distanceTraveled = 0f;
    private bool isCounting = true;

    private void Awake()
    {
        instance = this;
    }

    void Update()
    {
        if (!isCounting) return;

        distanceTraveled += speed * Time.deltaTime;
        distanceText.text = Mathf.FloorToInt(distanceTraveled).ToString() + " m";
    }

    public void SaveNewRecord()
    {
        float lastBestRecords = PlayerPrefs.GetFloat("BestRecord", 0);

        if (distanceTraveled > lastBestRecords)
        {
            PlayerPrefs.SetFloat("BestRecord", distanceTraveled);

            PlayerPrefs.Save();
        }
    }

    public float GetDistance()
    {
        return distanceTraveled;
    }

    public void ResetCounter()
    {
        distanceTraveled = 0f;
    }

    public void StopCounting()
    {
        isCounting = false;
    }

    public void ResumeCounting()
    {
        isCounting = true;
    }
}
