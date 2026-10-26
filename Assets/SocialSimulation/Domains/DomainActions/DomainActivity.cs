using SocialSimulation.Core;

namespace SocialSimulation.Domains.DomainActions
{
    public static class DomainActivity
    {
        public static bool Participated(SimulationWorld world, string sessionId, string actorId)
        {
            if (string.IsNullOrEmpty(sessionId) || !world.Activities.Sessions.TryGetValue(sessionId, out var session)) return false;
            foreach (var person in session.Participants)
                if (person.AgentId == actorId && (person.Status == "Participating" || person.Status == "Completed")) return true;
            return false;
        }
    }
}
