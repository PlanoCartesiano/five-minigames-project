using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MovementHandler : MonoBehaviour
{
    public Rigidbody2D rigidB;

    [Header("Movement Info")]
    [SerializeField] private float moveSpeed;
    [SerializeField] private float jumpForce;
    

    [Header("Collision Info")]
    [SerializeField] private float groundCheckDistance;
    [SerializeField] private LayerMask whatIsGround;
    [SerializeField] private Transform wallCheck;
    [SerializeField] private Vector2 wallCheckSize;

    private bool isGrounded;
    private bool wallDetected;

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
        wallDetected = Physics2D.BoxCast(wallCheck.position, wallCheckSize, 0, Vector2.zero, 0, whatIsGround);
    }
    private void OnDrawGizmos()
    {
        Gizmos.DrawLine(transform.position, new Vector2(transform.position.x, transform.position.y - groundCheckDistance));
        Gizmos.DrawWireCube(wallCheck.position, wallCheckSize);
    }
    #endregion
}

