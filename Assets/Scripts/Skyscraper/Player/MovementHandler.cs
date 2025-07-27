using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MovementHandler : MonoBehaviour
{
    public Rigidbody2D rigidB;

    [Header("Movement Info")]
    public float moveSpeed;
    public float jumpForce;
    

    [Header("Collision Info")]
    public float groundCheckDistance;
    public LayerMask whatIsGround;
    private bool isGrounded;

    void Start()
    {

    }

    void Update()
    {
        Move();
        Jump();
    }

    private void FixedUpdate()
    {
        CheckCollision();
    }

    #region Move
    void Move()
    {
            rigidB.velocity = new Vector2(moveSpeed, rigidB.velocity.y);
    }
    #endregion

    #region Jump
    void Jump()
    {
        if (Input.GetButtonDown("Jump") && isGrounded == true)
        {
            rigidB.velocity = new Vector2(rigidB.velocity.x, jumpForce);
        }
    }
    #endregion

    #region Collision
    void CheckCollision()
    {
        isGrounded = Physics2D.Raycast(transform.position, Vector2.down, groundCheckDistance, whatIsGround);
    }
    private void OnDrawGizmos()
    {
        Gizmos.DrawLine(transform.position, new Vector2(transform.position.x, transform.position.y - groundCheckDistance));
    }
    #endregion
}

