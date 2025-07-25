using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MovementHandler : MonoBehaviour
{
    public float moveSpeed;
    public float jumpForce;
    public Rigidbody2D rigidB;

    void Start()
    {
        
    }

    void Update()
    {
        rigidB.velocity = new Vector2(moveSpeed, rigidB.velocity.y);

        if(Input.GetButtonDown("Jump"))
        { 
            rigidB.velocity = new Vector2(rigidB.velocity.x, jumpForce);
        }
    }
}

