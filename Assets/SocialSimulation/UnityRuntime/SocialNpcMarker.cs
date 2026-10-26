using System;
using System.Collections.Generic;
using UnityEngine;
using SocialSimulation.Core;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>Connects a visible placeholder or imported character to one simulation entity.</summary>
    [DisallowMultipleComponent]
    public sealed class SocialNpcMarker : MonoBehaviour
    {
        [Serializable]
        public sealed class MembershipRole
        {
            public string CollectiveId;
            public string RoleId;
        }

        [SerializeField] private string npcInstanceId;
        [SerializeField] private string displayName;
        [SerializeField] private string primaryRoleId;
        [SerializeField] private string[] membershipIds = new string[0];
        [SerializeField] private Animator animator;
        [SerializeField] private string packageId;
        [SerializeField] private string primaryCollectiveId;
        [SerializeField] private List<MembershipRole> membershipRoles = new List<MembershipRole>();
        [SerializeField] private PersonalityArchetype personalityArchetype = new PersonalityArchetype();

        public string NpcInstanceId => npcInstanceId;
        public string DisplayName => displayName;
        public string PrimaryRoleId => primaryRoleId;
        public string[] MembershipIds => membershipIds;
        public Animator Animator => animator;
        public string PackageId => packageId;
        public PersonalityArchetype PersonalityArchetype => personalityArchetype?.Copy();
        public string PrimaryCollectiveId => primaryCollectiveId;
        public IReadOnlyList<MembershipRole> MembershipRoles => membershipRoles ??
            (IReadOnlyList<MembershipRole>)Array.Empty<MembershipRole>();

        public void Configure(string id, string label, string role, params string[] memberships)
        {
            npcInstanceId = id;
            displayName = label;
            primaryRoleId = role;
            membershipIds = memberships ?? new string[0];
            primaryCollectiveId = membershipIds.Length == 0 ? null : membershipIds[0];
            if (membershipRoles == null) membershipRoles = new List<MembershipRole>();
            else membershipRoles.Clear();
        }

        public void SetAnimator(Animator value)
        {
            animator = value;
        }

        public void BindPackage(string id) => packageId = id;

        public void SetPersonalityArchetype(PersonalityArchetype value)
        {
            PersonalityGenerator.Validate(value);
            personalityArchetype = value.Copy();
        }

        public void SetPrimaryCollective(string id)
        {
            if (Array.IndexOf(membershipIds, id) < 0)
                throw new ArgumentException("Primary collective must be one of the NPC memberships.");
            primaryCollectiveId = id;
        }

        public void BindMembershipRole(string collectiveId, string roleId)
        {
            if (Array.IndexOf(membershipIds, collectiveId) < 0 || string.IsNullOrWhiteSpace(roleId))
                throw new ArgumentException("Membership role must target an existing membership.");
            if (membershipRoles == null) membershipRoles = new List<MembershipRole>();
            foreach (var binding in membershipRoles)
                if (binding.CollectiveId == collectiveId)
                { binding.RoleId = roleId; return; }
            membershipRoles.Add(new MembershipRole { CollectiveId = collectiveId, RoleId = roleId });
        }
    }
}
