using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class Records : MonoBehaviour
{
    public TextMeshProUGUI bestRecordDistance;

    void Start()
    {
        LoadBestRecords();
    }

    void LoadBestRecords()
    {
        float best = PlayerPrefs.GetFloat("BestRecord", 0);

        bestRecordDistance.text = "dd/mm/yy - " + Mathf.FloorToInt(best).ToString() + " meters (m)";
    }
}
