using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MovementHandler : MonoBehaviour
{
    public Rigidbody2D rigidB;

    [Header("Movement Info")]
    [SerializeField] private float moveSpeed;
    [SerializeField] private float jumpForce;

    [Header("Slide Info")]
    [SerializeField] private float slideSpeed;
    [SerializeField] private float slideTime;
    private float slideTimeCounter;
    private bool isSliding;

    [Header("Collision Info")]
    [SerializeField] private float groundCheckDistance;
    [SerializeField] private LayerMask whatIsGround;
    [SerializeField] private Transform wallCheck;
    [SerializeField] private Vector2 wallCheckSize;

    private bool isGrounded;
    private bool wallDetected;
    private bool playerUnlocked = true;

    void Start()
    {

    }

    void Update()
    {
        Move();
        Jump();
        SlideCounter();
        CheckSlideInput();
        CheckForSlide();
    }

    private void FixedUpdate()
    {
        CheckCollision();
    }

    #region Move
    void Move()
    {
        if (playerUnlocked && !wallDetected)
        {
            if (isSliding)
            {
                Slide();
            }
            else
            {
                rigidB.velocity = new Vector2(moveSpeed, rigidB.velocity.y);
            }
        }
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

    #region Slide
    void Slide()
    {
        if (isSliding)
        {
            rigidB.velocity = new Vector2(slideSpeed, rigidB.velocity.y);
        }
    }

    void CheckSlideInput()
    { 
        if (Input.GetButtonDown("Fire1") && isGrounded == true)
        {
            isSliding = true;
            slideTimeCounter = slideTime;
        }
    }
    
    void SlideCounter()
    {
        slideTimeCounter -= Time.deltaTime;
    }

    void CheckForSlide()
    {
        if (slideTimeCounter < 0)
        {
            isSliding = false;
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

