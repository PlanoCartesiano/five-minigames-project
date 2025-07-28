using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DeadZoneTrigger : MonoBehaviour
{

    public Transform player;
    public float offset = -5f;

    private void OnTriggerEnter2D(Collider2D collision)
    {
        switch (collision.gameObject.tag)
        {
            case "Player":
                GameManager.instance.RestartLevel();
            break;
        }
    }
    void Update()
    {
        if (player != null)
        {
            transform.position = new Vector3(player.position.x, offset, transform.position.z);
        }

    }
}
