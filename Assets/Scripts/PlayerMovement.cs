using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class PlayerMovement : MonoBehaviour
{
    // Celeste-style controls:
    //   Arrow keys = move / aim dash / climb up and down while grabbing a wall
    //   C          = jump (wall jump when touching a wall)
    //   X          = dash
    //   Z (hold)   = grab wall

    [Header("Movement")]
    [Range(1f, 15f)] public float moveSpeed = 5f;   // run speed
    [Range(1f, 30f)] public float jumpForce = 12f;  // jump strength
    [Range(0.5f, 10f)] public float gravity = 3f;   // higher gravity = faster fall

    [Header("Dash")]
    [Range(5f, 50f)] public float dashSpeed = 20f;          // dash speed / force
    [Range(0.05f, 0.5f)] public float dashDuration = 0.15f; // how long a dash lasts (seconds)
    [Range(0f, 1f)] public float afterDashSpeed = 0.5f;     // how much speed is kept after a dash (0 = stop immediately)

    [Header("Wall Grab (hold Z)")]
    [Range(0.02f, 0.3f)] public float wallCheckDistance = 0.1f; // distance used to detect walls
    [Range(1f, 10f)] public float climbUpSpeed = 4.5f;          // climbing speed
    [Range(1f, 15f)] public float climbDownSpeed = 8f;          // speed when sliding down while grabbing
    [Range(1f, 10f)] public float wallSlideSpeed = 3f;          // slide speed when touching a wall (not grabbing)
    [Range(10f, 300f)] public float maxStamina = 110f;          // maximum stamina
    [Range(0f, 100f)] public float climbStaminaCost = 45f;      // stamina lost per second while climbing up
    [Range(0f, 50f)] public float holdStaminaCost = 10f;        // stamina lost per second while just holding on
    [Range(0f, 100f)] public float wallJumpStaminaCost = 27.5f; // stamina lost when wall jumping while grabbing

    [Header("Wall Jump")]
    [Range(1f, 20f)] public float wallJumpForceX = 8f;          // push away from the wall
    [Range(1f, 30f)] public float wallJumpForceY = 12f;         // upward force
    [Range(0.05f, 0.5f)] public float wallJumpLockTime = 0.15f; // time horizontal control is locked after a wall jump

    [Header("Dash Afterimage")]
    public bool enableGhost = true;                                    // turn the afterimage on/off
    public Color ghostColor = new Color(0.4f, 0.8f, 1f, 0.7f);         // afterimage color
    [Range(0.01f, 0.1f)] public float ghostInterval = 0.03f;           // time between two afterimages (smaller = denser trail)
    [Range(0.1f, 1f)] public float ghostFadeTime = 0.3f;               // time for an afterimage to fade away

    [Header("State (view only)")]
    [SerializeField] private float stamina;

    private Rigidbody2D rb;
    private Collider2D col;
    private SpriteRenderer sr;
    private float moveInput;
    private bool isGrounded;

    // Wall grab
    private ContactFilter2D wallFilter;
    private RaycastHit2D[] wallHits = new RaycastHit2D[4];
    private int wallDir;           // -1 = wall on the left, 1 = wall on the right, 0 = no wall
    private bool isGrabbing;
    private float climbVelocity;
    private float wallJumpLockTimer;

    // Dash
    private bool isDashing;
    private bool canDash = true;   // one dash in the air; touching the ground refills it
    private float dashTimer;
    private float ghostTimer;
    private Vector2 dashDirection;
    private float facing = 1f;     // facing direction: 1 = right, -1 = left

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        col = GetComponent<Collider2D>();
        sr = GetComponentInChildren<SpriteRenderer>();

        wallFilter = new ContactFilter2D();
        wallFilter.useTriggers = false;

        stamina = maxStamina;
    }

    void Update()
    {
        // Arrow keys only
        moveInput = GetArrowAxis(KeyCode.LeftArrow, KeyCode.RightArrow);
        float verticalInput = GetArrowAxis(KeyCode.DownArrow, KeyCode.UpArrow);

        // Remember facing direction (does not change while grabbing a wall)
        if (moveInput != 0f && !isGrabbing)
        {
            facing = Mathf.Sign(moveInput);
        }

        wallDir = GetWallDirection();
        wallJumpLockTimer -= Time.deltaTime;

        // Touching the ground refills dash and stamina (not while dashing)
        if (isGrounded && !isDashing)
        {
            canDash = true;
            stamina = maxStamina;
        }

        // While dashing: count down, spawn afterimages, block running and jumping
        if (isDashing)
        {
            ghostTimer -= Time.deltaTime;
            if (ghostTimer <= 0f)
            {
                SpawnGhost();
                ghostTimer = ghostInterval;
            }

            dashTimer -= Time.deltaTime;
            if (dashTimer <= 0f)
            {
                EndDash();
            }
            return;
        }

        // Hold Z + touching a wall + facing the wall + has stamina -> grab the wall
        isGrabbing = Input.GetKey(KeyCode.Z)
                  && wallDir != 0
                  && facing == wallDir
                  && stamina > 0f
                  && wallJumpLockTimer <= 0f;

        if (isGrabbing)
        {
            rb.gravityScale = 0f;
            climbVelocity = 0f;

            if (verticalInput > 0f)
            {
                climbVelocity = climbUpSpeed;
                stamina -= climbStaminaCost * Time.deltaTime;
            }
            else if (verticalInput < 0f)
            {
                climbVelocity = -climbDownSpeed;
            }
            else
            {
                stamina -= holdStaminaCost * Time.deltaTime;
            }

            stamina = Mathf.Max(0f, stamina);
        }
        else
        {
            // Apply gravity from the Inspector (can be tweaked while the game is running)
            rb.gravityScale = gravity;
        }

        // Press X to dash in the direction being held (8 directions)
        if (Input.GetKeyDown(KeyCode.X) && canDash)
        {
            StartDash(moveInput, verticalInput);
            return;
        }

        // Press C to jump
        if (Input.GetKeyDown(KeyCode.C))
        {
            if (isGrounded)
            {
                // Jump from the ground
                rb.linearVelocity = new Vector2(rb.linearVelocity.x, jumpForce);
                isGrounded = false;
            }
            else if (wallDir != 0)
            {
                // Jump off the wall
                WallJump();
            }
        }
    }

    void FixedUpdate()
    {
        if (isDashing)
        {
            // Dash: fly straight in the chosen direction at a fixed speed
            rb.linearVelocity = dashDirection * dashSpeed;
            return;
        }

        if (isGrabbing)
        {
            // Wall grab: hold still or climb up/down
            rb.linearVelocity = new Vector2(0f, climbVelocity);
            return;
        }

        if (wallJumpLockTimer > 0f)
        {
            // Just wall jumped: horizontal control is locked briefly so you get pushed away from the wall
            return;
        }

        // Move left/right, keep vertical velocity (jumping/falling)
        Vector2 velocity = new Vector2(moveInput * moveSpeed, rb.linearVelocity.y);

        // Touching a wall in the air while pushing toward it -> slide down slowly
        if (wallDir != 0 && !isGrounded && moveInput == wallDir && velocity.y < -wallSlideSpeed)
        {
            velocity.y = -wallSlideSpeed;
        }

        rb.linearVelocity = velocity;
    }

    // Returns -1, 0 or 1 from two keys (negative, positive)
    float GetArrowAxis(KeyCode negative, KeyCode positive)
    {
        float value = 0f;
        if (Input.GetKey(negative)) value -= 1f;
        if (Input.GetKey(positive)) value += 1f;
        return value;
    }

    // -1: wall on the left, 1: wall on the right, 0: no wall
    int GetWallDirection()
    {
        if (col == null) return 0;

        int count = col.Cast(Vector2.left, wallFilter, wallHits, wallCheckDistance);
        for (int i = 0; i < count; i++)
        {
            if (wallHits[i].normal.x > 0.7f) return -1;
        }

        count = col.Cast(Vector2.right, wallFilter, wallHits, wallCheckDistance);
        for (int i = 0; i < count; i++)
        {
            if (wallHits[i].normal.x < -0.7f) return 1;
        }

        return 0;
    }

    void WallJump()
    {
        // Spend stamina if grabbing the wall
        if (isGrabbing)
        {
            stamina = Mathf.Max(0f, stamina - wallJumpStaminaCost);
        }

        isGrabbing = false;
        rb.gravityScale = gravity;

        // Jump up and away from the wall
        rb.linearVelocity = new Vector2(-wallDir * wallJumpForceX, wallJumpForceY);
        wallJumpLockTimer = wallJumpLockTime;
        facing = -wallDir;
    }

    void StartDash(float horizontal, float vertical)
    {
        Vector2 direction = new Vector2(horizontal, vertical);

        // No direction held -> dash the way the player is facing
        if (direction == Vector2.zero)
        {
            direction = new Vector2(facing, 0f);
        }

        dashDirection = direction.normalized; // normalized so diagonal dashes are not faster
        isDashing = true;
        isGrabbing = false;
        canDash = false;
        dashTimer = dashDuration;
        ghostTimer = 0f; // spawn the first afterimage immediately
        rb.gravityScale = 0f; // no gravity during the dash
    }

    void EndDash()
    {
        isDashing = false;
        // Keep part of the dash speed so the movement does not stop abruptly
        rb.linearVelocity = dashDirection * dashSpeed * afterDashSpeed;
    }

    // Create a copy of the character at the current position that fades out
    void SpawnGhost()
    {
        if (!enableGhost || sr == null || sr.sprite == null) return;

        GameObject ghost = new GameObject("DashGhost");
        ghost.transform.position = sr.transform.position;
        ghost.transform.rotation = sr.transform.rotation;
        ghost.transform.localScale = sr.transform.lossyScale;

        SpriteRenderer ghostRenderer = ghost.AddComponent<SpriteRenderer>();
        ghostRenderer.sprite = sr.sprite;
        ghostRenderer.flipX = sr.flipX;
        ghostRenderer.flipY = sr.flipY;
        ghostRenderer.sortingLayerID = sr.sortingLayerID;
        ghostRenderer.sortingOrder = sr.sortingOrder - 1; // drawn behind the character
        ghostRenderer.color = ghostColor;

        StartCoroutine(FadeGhost(ghostRenderer));
    }

    IEnumerator FadeGhost(SpriteRenderer ghostRenderer)
    {
        Color startColor = ghostRenderer.color;
        float t = 0f;

        while (t < ghostFadeTime)
        {
            if (ghostRenderer == null) yield break;

            t += Time.deltaTime;
            float alpha = Mathf.Lerp(startColor.a, 0f, t / ghostFadeTime);
            ghostRenderer.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
            yield return null;
        }

        if (ghostRenderer != null)
        {
            Destroy(ghostRenderer.gameObject);
        }
    }

    
    void OnCollisionStay2D(Collision2D collision)
    {
        foreach (ContactPoint2D contact in collision.contacts)
        {
            if (contact.normal.y > 0.5f)
            {
                isGrounded = true;
                return;
            }
        }
    }

    void OnCollisionExit2D(Collision2D collision)
    {
        isGrounded = false;
    }
}