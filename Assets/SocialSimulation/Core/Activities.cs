using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class ActivityParticipant
    {
        public string AgentId;
        public string Role;
        public string Status = "Invited";
        public double JoinedAt;
        public double LeftAt;
        public string ResourceTokenId;
    }

    [Serializable]
    public sealed class ActivitySession
    {
        public string Id;
        public string DefinitionId;
        public string Kind;
        public string PlaceId;
        public string OrganizerId;
        public string State = "Inviting";
        public double CreatedAt;
        public double EarliestStart;
        public double Deadline;
        public double EndsAt;
        public int MinimumParticipants = 2;
        public int MaximumParticipants = 8;
        public string ResultId;
        public List<ActivityParticipant> Participants = new List<ActivityParticipant>();
    }

    public sealed class ActivityService
    {
        private readonly SimulationWorld world;
        public readonly Dictionary<string, ActivitySession> Sessions = new Dictionary<string, ActivitySession>(StringComparer.Ordinal);
        internal ActivityService(SimulationWorld world) { this.world = world; }

        public ActivitySession Create(string kind, string organizerId, string placeId, double start, double deadline,
            double duration, int minimum = 2, int maximum = 8)
        {
            if (!world.Agents.ContainsKey(organizerId) || minimum < 1 || maximum < minimum ||
                !SimulationWorld.Finite(start) || !SimulationWorld.Finite(deadline) || !SimulationWorld.Finite(duration) ||
                !SimulationWorld.Finite(start + duration) || deadline < start || duration <= 0)
                throw new ArgumentException("Invalid activity configuration.");
            var session = new ActivitySession { Id = world.NextId(), Kind = kind, OrganizerId = organizerId,
                PlaceId = placeId, CreatedAt = world.Time, EarliestStart = start, Deadline = deadline,
                EndsAt = start + duration, MinimumParticipants = minimum, MaximumParticipants = maximum };
            session.Participants.Add(new ActivityParticipant { AgentId = organizerId, Role = "Organizer", Status = "Accepted" });
            Sessions.Add(session.Id, session);
            world.Emit("activity.created", organizerId, session.Id, kind, placeId);
            return session;
        }

        public bool Invite(string sessionId, string agentId, string role = "Participant")
        {
            if (!Sessions.TryGetValue(sessionId, out var session) || session.State != "Inviting" ||
                !world.Agents.ContainsKey(agentId)) return false;
            int openPlaces = 0;
            foreach (var person in session.Participants)
                if (person.Status == "Invited" || person.Status == "Accepted" ||
                    person.Status == "Participating") openPlaces++;
            if (openPlaces >= session.MaximumParticipants) return false;
            foreach (var person in session.Participants) if (person.AgentId == agentId) return false;
            session.Participants.Add(new ActivityParticipant { AgentId = agentId, Role = role });
            world.Emit("activity.invited", session.OrganizerId, agentId, sessionId, session.PlaceId);
            return true;
        }

        public bool Respond(string sessionId, string agentId, bool accept)
        {
            if (!Sessions.TryGetValue(sessionId, out var session) || session.State != "Inviting") return false;
            foreach (var person in session.Participants)
                if (person.AgentId == agentId && person.Status == "Invited")
                {
                    person.Status = accept ? "Accepted" : "Declined";
                    world.Emit(accept ? "activity.accepted" : "activity.declined", agentId, sessionId, null, session.PlaceId);
                    return true;
                }
            return false;
        }

        public bool Start(string sessionId)
        {
            if (!Sessions.TryGetValue(sessionId, out var session) || session.State != "Inviting" ||
                world.Time < session.EarliestStart || world.Time > session.Deadline || world.Time >= session.EndsAt) return false;
            int present = 0;
            foreach (var person in session.Participants)
                if (person.Status == "Accepted" && world.Agents.TryGetValue(person.AgentId, out var agent) &&
                    agent.PlaceId == session.PlaceId) present++;
            if (present < session.MinimumParticipants) return false;
            session.State = "Active";
            foreach (var person in session.Participants)
                if (person.Status == "Accepted" && world.Agents.TryGetValue(person.AgentId, out var agent) &&
                    agent.PlaceId == session.PlaceId)
                { person.Status = "Participating"; person.JoinedAt = world.Time; }
            world.Emit("activity.started", session.OrganizerId, sessionId, null, session.PlaceId);
            return true;
        }

        public bool JoinLate(string sessionId, string agentId)
        {
            if (!Sessions.TryGetValue(sessionId, out var session) || session.State != "Active" || world.Time >= session.EndsAt) return false;
            foreach (var person in session.Participants)
                if (person.AgentId == agentId && person.Status == "Accepted" &&
                    world.Agents.TryGetValue(agentId, out var agent) && agent.PlaceId == session.PlaceId)
                { person.Status = "Participating"; person.JoinedAt = world.Time; return true; }
            return false;
        }

        public void Leave(string sessionId, string agentId)
        {
            if (!Sessions.TryGetValue(sessionId, out var session)) return;
            foreach (var person in session.Participants)
                if (person.AgentId == agentId && (person.Status == "Invited" || person.Status == "Accepted" || person.Status == "Participating"))
                {
                    person.Status = "Left";
                    person.LeftAt = world.Time;
                    if (!string.IsNullOrEmpty(person.ResourceTokenId)) world.Resources.Release(person.ResourceTokenId, sessionId);
                    world.Emit("activity.left", agentId, sessionId, null, session.PlaceId);
                }
            if (session.State == "Active")
            {
                int present = 0;
                foreach (var person in session.Participants) if (person.Status == "Participating") present++;
                if (present < session.MinimumParticipants) Cancel(sessionId, "insufficient-participants");
            }
        }

        public void LeaveAll(string agentId)
        {
            foreach (var session in Sessions.Values) Leave(session.Id, agentId);
        }

        public bool AttachResource(string sessionId, string agentId, string tokenId, string oldOwnerScopeId)
        {
            if (!Sessions.TryGetValue(sessionId, out var session) ||
                (session.State != "Inviting" && session.State != "Active") ||
                !world.Resources.Tokens.TryGetValue(tokenId ?? string.Empty, out var token) ||
                token.AgentId != agentId) return false;
            foreach (var person in session.Participants)
                if (person.AgentId == agentId && (person.Status == "Accepted" || person.Status == "Participating"))
                {
                    if (!string.IsNullOrEmpty(person.ResourceTokenId)) return false;
                    if (!world.Resources.Transfer(tokenId, oldOwnerScopeId, sessionId)) return false;
                    person.ResourceTokenId = tokenId;
                    return true;
                }
            return false;
        }

        public bool Complete(string sessionId, string resultKind)
        {
            if (!Sessions.TryGetValue(sessionId, out var session) || session.State != "Active") return false;
            session.State = "Completed";
            session.ResultId = world.NextId();
            foreach (var person in session.Participants)
            {
                if (person.Status == "Participating") { person.Status = "Completed"; person.LeftAt = world.Time; }
                else if (person.Status == "Accepted" || person.Status == "Invited") person.Status = "Expired";
                if (!string.IsNullOrEmpty(person.ResourceTokenId)) world.Resources.Release(person.ResourceTokenId, sessionId);
            }
            world.Resources.ReleaseScope(sessionId);
            world.TryCommitEffect(session.Id + ":result", () => world.Emit("activity.completed", session.OrganizerId,
                session.Id, resultKind + ":" + session.ResultId, session.PlaceId));
            return true;
        }

        public void Cancel(string sessionId, string reason)
        {
            if (!Sessions.TryGetValue(sessionId, out var session) || session.State == "Completed" || session.State == "Cancelled") return;
            session.State = "Cancelled";
            foreach (var person in session.Participants)
                if (person.Status == "Invited" || person.Status == "Accepted" || person.Status == "Participating")
                { person.Status = "Cancelled"; person.LeftAt = world.Time; }
            world.Resources.ReleaseScope(sessionId);
            world.Emit("activity.cancelled", session.OrganizerId, sessionId, reason, session.PlaceId);
        }

        public void Tick()
        {
            var ids = new List<string>(Sessions.Keys); ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var session = Sessions[id];
                if (session.State == "Inviting" && world.Time >= session.Deadline)
                {
                    if (!Start(id)) Cancel(id, "deadline");
                }
                else if (session.State == "Active" && world.Time >= session.EndsAt) Complete(id, "duration");
            }
        }
    }
}
