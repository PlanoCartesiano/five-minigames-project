using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MovementHandler : MonoBehaviour
{
    public Rigidbody2D rigidB;
    private bool playerUnlocked = true;
    private bool isDead = false;
    private bool isGrounded;
    private bool wallDetected;

    [Header("Movement Info")]
    [SerializeField] private float moveSpeed;
    [SerializeField] private float jumpForce;

    [Header("Slide Info")]
    [SerializeField] private float slideSpeed;
    [SerializeField] private float slideTime;
    [SerializeField] private float slideCooldown;
    [SerializeField] private float slideExtraGravity = 10f;
    private float slideCooldownCounter;
    private float slideTimeCounter;
    private bool isSliding;

    [Header("Collision Info")]
    [SerializeField] private float groundCheckDistance;
    [SerializeField] private LayerMask whatIsGround;
    [SerializeField] private Transform wallCheck;
    [SerializeField] private Vector2 wallCheckSize;

    void Awake()
    {
        rigidB = GetComponent<Rigidbody2D>();
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
        else if (isDead)
        {
            rigidB.velocity = Vector2.zero;
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
            if (isGrounded)
            {
                rigidB.velocity = new Vector2(slideSpeed, rigidB.velocity.y);
            }

            if (!isGrounded)
            {
                rigidB.velocity = new Vector2(slideSpeed, -slideExtraGravity);
            }
        }
    }

    void CheckSlideInput()
    {
        if (Input.GetButtonDown("Fire1") && slideCooldownCounter < 0) 
        {
            isSliding = true;
            slideTimeCounter = slideTime;
            slideCooldownCounter = slideCooldown;
        }
    }

    void SlideCounter()
    {
        slideTimeCounter -= Time.deltaTime;
        slideCooldownCounter -= Time.deltaTime;
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

    private void OnTriggerEnter2D(Collider2D collision)
    {
        switch (collision.gameObject.tag)
        {
            case "Danger":
                StartCoroutine(Die());
            break;
                
        }
    }

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

    #region Death

    public IEnumerator Die()
    {
        isDead = true;
        playerUnlocked = false;
        yield return new WaitForSeconds(0.5f);
        rigidB.velocity = new Vector2(0f, 0f);
        GameManager.instance.RestartLevel();
    }
    #endregion


}

