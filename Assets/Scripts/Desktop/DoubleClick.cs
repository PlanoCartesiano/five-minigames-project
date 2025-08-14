using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

public class DoubleClick : MonoBehaviour, IPointerClickHandler
{
    [SerializeField] private float doubleClickThreshold = 0.3f;
    public UnityEvent onDoubleClick;

    private float lastClickTime;

    public void OnPointerClick(PointerEventData eventData)
    {
        float timeSinceLastClick = Time.time - lastClickTime;

        if (timeSinceLastClick <= doubleClickThreshold)
        {
            onDoubleClick?.Invoke();
            lastClickTime = 0f;
        }
        else
        {
            lastClickTime = Time.time;
        }
    }
}
