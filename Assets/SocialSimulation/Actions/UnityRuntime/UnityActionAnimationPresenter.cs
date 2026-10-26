using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SocialSimulation.Core;
using SocialSimulation.UnityRuntime;
using UnityEngine;

namespace SocialSimulation.Actions
{
    /// <summary>Shared visual vocabulary. Domains map their action IDs to these cues.</summary>
    public enum ActionVisualCue
    {
        None = 0,
        Travel = 1,
        Sit = 2,
        Converse = 3,
        Work = 4,
        Trade = 5,
        Group = 6,
        Carry = 7
    }

    /// <summary>Projects semantic action events onto optional Animator parameters.</summary>
    public sealed class UnityActionAnimationPresenter : IWorldActionPresentation
    {
        private sealed class VisualState
        {
            public string InstanceId;
            public ActionVisualCue Cue;
            public ActionPhase Phase;
            public long Order;
        }

        public const string ActiveParameter = "ActionActive";
        public const string CueParameter = "ActionCue";
        public const string PhaseParameter = "ActionPhase";

        private readonly SocialWorldRunner runner;
        private readonly IReadOnlyDictionary<string, ActionVisualCue> cues;
        private sealed class SharedPresentation
        {
            public SimulationWorld World;
            public long Order;
            public int NextOwner;
            public readonly Dictionary<string, Dictionary<string, VisualState>> Actors =
                new Dictionary<string, Dictionary<string, VisualState>>(StringComparer.Ordinal);
        }
        private static readonly ConditionalWeakTable<SocialWorldRunner, SharedPresentation> Shared =
            new ConditionalWeakTable<SocialWorldRunner, SharedPresentation>();
        private static readonly int ActiveHash = Animator.StringToHash(ActiveParameter);
        private static readonly int CueHash = Animator.StringToHash(CueParameter);
        private static readonly int PhaseHash = Animator.StringToHash(PhaseParameter);
        private readonly SharedPresentation shared;
        private readonly string owner;

        public UnityActionAnimationPresenter(SocialWorldRunner runner,
            IReadOnlyDictionary<string, ActionVisualCue> cues = null)
        {
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
            this.cues = cues;
            shared = Shared.GetOrCreateValue(runner);
            owner = (++shared.NextOwner).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
        }

        public void OnActionPresentation(ActionPresentationEvent entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.ActorId) ||
                string.IsNullOrEmpty(entry.ActionInstanceId)) return;
            if (!ReferenceEquals(shared.World, runner.World))
            {
                shared.World = runner.World;
                shared.Actors.Clear();
                shared.Order = 0;
                foreach (var npc in runner.NpcViews.Values)
                    if (npc != null) SocialAnimatorParameters.SetBool(npc.Animator, ActiveHash, false);
            }
            var key = owner + entry.ActionInstanceId;
            if (!shared.Actors.TryGetValue(entry.ActorId, out var states))
            {
                states = new Dictionary<string, VisualState>(StringComparer.Ordinal);
                shared.Actors.Add(entry.ActorId, states);
            }
            if (entry.Moment == ActionPresentationMoment.Exited)
            {
                states.Remove(key);
                if (states.Count == 0) shared.Actors.Remove(entry.ActorId);
            }
            else
            {
                if (!states.TryGetValue(key, out var state))
                {
                    state = new VisualState { InstanceId = key, Order = ++shared.Order };
                    states.Add(key, state);
                }
                state.Cue = cues != null && cues.TryGetValue(entry.ActionId, out var cue)
                    ? cue : ActionVisualCue.None;
                state.Phase = entry.Phase;
            }
            Apply(entry.ActorId, states);
        }

        private void Apply(string actorId, Dictionary<string, VisualState> states)
        {
            if (!runner.NpcViews.TryGetValue(actorId, out var npc) || npc == null ||
                npc.Animator == null || npc.Animator.runtimeAnimatorController == null) return;
            var animator = npc.Animator;
            VisualState selected = null;
            foreach (var state in states.Values)
                if (state.Cue != ActionVisualCue.None && state.Cue != ActionVisualCue.Travel &&
                    (selected == null || state.Order > selected.Order)) selected = state;
            SocialAnimatorParameters.SetBool(animator, ActiveHash, selected != null);
            SocialAnimatorParameters.SetInteger(animator, CueHash,
                selected == null ? 0 : (int)selected.Cue);
            SocialAnimatorParameters.SetInteger(animator, PhaseHash,
                selected == null ? 0 : (int)selected.Phase);
        }

    }
}
