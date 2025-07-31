using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class DistanceCounter : MonoBehaviour
{
    [Header("UI Reference")]
    public TextMeshProUGUI distanceText;

    [Header("Settings")]
    public float speed = 6f;

    private float distanceTraveled = 0f;
    private bool isCounting = true;

    void Update()
    {
        if (!isCounting) return;

        distanceTraveled += speed * Time.deltaTime;
        distanceText.text = Mathf.FloorToInt(distanceTraveled).ToString() + " m";
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
