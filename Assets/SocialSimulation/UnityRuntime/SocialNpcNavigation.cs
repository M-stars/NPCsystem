using UnityEngine;
using UnityEngine.AI;

namespace SocialSimulation.UnityRuntime
{
    public enum SocialNavigationStatus
    {
        Idle,
        InProgress,
        Succeeded,
        Failed,
        Cancelled
    }

    /// <summary>
    /// NavMesh presentation adapter. The simulation owns intentions and effects; callers poll this
    /// adapter and submit the observed result at the next simulation Tick.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class SocialNpcNavigation : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float targetSearchRadius = 1.5f;
        [SerializeField, Min(0.05f)] private float arrivalTolerance = 0.35f;
        [SerializeField, Min(0.1f)] private float walkSpeed = 1.4f;
        [SerializeField, Min(0.1f)] private float runSpeed = 3f;

        private NavMeshAgent agent;
        private bool started;
        private bool activationPending;
        private int requestGeneration;
        private bool paused;
        private float pausedAt;
        private float requestRealtime;
        private Vector3 requestedDestination;
        private int retryCount;
        private string lastFailureReason;
        private SocialNavigationStatus status;

        public int RequestGeneration => requestGeneration;
        public SocialNavigationStatus Status => status;
        public Vector3 Position => transform.position;
        public bool IsMoving => agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh &&
                                !paused && agent.velocity.sqrMagnitude > 0.04f;
        public bool IsRunning => IsMoving && agent.speed > walkSpeed + 0.1f;
        public string LastFailureReason => lastFailureReason;

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            agent.speed = walkSpeed;
            activationPending = !agent.enabled;
        }

        private void Start()
        {
            // Scene surfaces register their baked data in OnEnable. Prefab agents remain disabled
            // until Start so native agent creation cannot precede that registration.
            started = true;
            TryPlaceOnNavMesh();
        }

        private void OnEnable()
        {
            if (!started) return;
            activationPending = true;
            TryPlaceOnNavMesh();
        }

        public void ConfigureSpeeds(float walking, float running)
        {
            walkSpeed = Mathf.Max(0.1f, walking);
            runSpeed = Mathf.Max(walkSpeed, running);
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (status != SocialNavigationStatus.InProgress) agent.speed = walkSpeed;
        }

        public SocialNavigationStatus RequestMove(Vector3 target, out int generation)
        { return RequestMove(target, false, out generation); }

        public SocialNavigationStatus RequestMove(Vector3 target, bool run, out int generation)
        {
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            requestGeneration++;
            generation = requestGeneration;
            status = SocialNavigationStatus.Failed;
            lastFailureReason = null;

            if (!TryPlaceOnNavMesh()) { lastFailureReason = "agent_off_navmesh"; return status; }
            if (!SamplePosition(target, out var hit))
            { lastFailureReason = "target_off_navmesh"; return status; }

            agent.speed = run ? runSpeed : walkSpeed;
            agent.isStopped = paused;
            if (!agent.SetDestination(hit.position))
            { lastFailureReason = "destination_rejected"; return status; }
            requestedDestination = hit.position;
            requestRealtime = Time.realtimeSinceStartup;
            retryCount = 0;
            status = SocialNavigationStatus.InProgress;
            return status;
        }

        public SocialNavigationStatus Poll(int generation)
        {
            if (generation != requestGeneration) return SocialNavigationStatus.Cancelled;
            if (status != SocialNavigationStatus.InProgress) return status;
            if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh)
            { lastFailureReason = "agent_left_navmesh"; return status = SocialNavigationStatus.Failed; }
            if (paused || agent.pathPending) return status;
            var offset = transform.position - requestedDestination;
            offset.y = 0f;
            if (!float.IsInfinity(agent.remainingDistance) &&
                agent.remainingDistance <= agent.stoppingDistance + arrivalTolerance &&
                offset.sqrMagnitude <= (agent.stoppingDistance + arrivalTolerance) *
                    (agent.stoppingDistance + arrivalTolerance) &&
                (!agent.hasPath || agent.velocity.sqrMagnitude <= 0.04f))
                return status = SocialNavigationStatus.Succeeded;
            if (!agent.hasPath || agent.pathStatus != NavMeshPathStatus.PathComplete)
            {
                var elapsed = Time.realtimeSinceStartup - requestRealtime;
                if (elapsed < 0.35f) return status;
                if (retryCount < 2)
                {
                    retryCount++;
                    agent.SetDestination(requestedDestination);
                    return status;
                }
                if (elapsed >= 2f)
                {
                    lastFailureReason = "path_timeout:" + agent.pathStatus + ":remaining=" +
                        agent.remainingDistance.ToString("0.00") + ":distance=" +
                        Vector3.Distance(transform.position, requestedDestination).ToString("0.00");
                    return status = SocialNavigationStatus.Failed;
                }
            }
            return status;
        }

        public void CancelMove()
        {
            requestGeneration++;
            status = SocialNavigationStatus.Cancelled;
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                agent.ResetPath();
            if (agent != null) agent.speed = walkSpeed;
        }

        public bool RestorePose(Vector3 position, Quaternion rotation)
        {
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (!isActiveAndEnabled || (!agent.isActiveAndEnabled && !activationPending) ||
                !SamplePosition(position, out var hit)) return false;
            CancelMove();
            // Restoring a valid saved pose must also work when the current pose is off the mesh.
            if (activationPending ? !ActivateAt(hit.position) : !agent.Warp(hit.position)) return false;
            transform.rotation = rotation;
            status = SocialNavigationStatus.Idle;
            return true;
        }

        public bool CanRestorePose(Vector3 position)
        {
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            return agent != null && isActiveAndEnabled && (agent.isActiveAndEnabled || activationPending) &&
                SamplePosition(position, out _);
        }

        public void SetSimulationPaused(bool value)
        {
            if (value && !paused) pausedAt = Time.realtimeSinceStartup;
            else if (!value && paused && status == SocialNavigationStatus.InProgress)
                requestRealtime += Time.realtimeSinceStartup - pausedAt;
            paused = value;
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                agent.isStopped = value;
        }

        private bool TryPlaceOnNavMesh()
        {
            if (!isActiveAndEnabled) return false;
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (activationPending)
            {
                if (!SamplePosition(transform.position, out var initial)) return false;
                if (!ActivateAt(initial.position)) return false;
            }
            if (!agent.isActiveAndEnabled) return false;
            if (agent.isOnNavMesh) return true;
            return SamplePosition(transform.position, out var hit) &&
                   agent.Warp(hit.position);
        }

        private bool ActivateAt(Vector3 position)
        {
            var previousPosition = transform.position;
            transform.position = position;
            agent.enabled = true;
            if (!agent.isOnNavMesh)
            {
                agent.enabled = false;
                transform.position = previousPosition;
                return false;
            }
            activationPending = false;
            agent.isStopped = paused;
            return true;
        }

        private bool SamplePosition(Vector3 position, out NavMeshHit hit)
        {
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            return NavMesh.SamplePosition(position, out hit, targetSearchRadius, filter);
        }

        private void OnDisable()
        {
            CancelMove();
            if (agent != null) agent.enabled = false;
            activationPending = true;
        }
    }
}
