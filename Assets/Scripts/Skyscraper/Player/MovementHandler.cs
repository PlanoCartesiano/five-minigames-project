using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MovementHandler : MonoBehaviour
{
    public float moveSpeed;
    public int jumpForce;
    public Rigidbody2D rigidB;

    void Start()
    {
        
    }

    void FixedUpdate()
    {
        Move();
    }

    #region Move 
    private void Move()
    {
        rigidB.velocity = new Vector2(moveSpeed, rigidB.velocity.y);
    }
    #endregion
}

