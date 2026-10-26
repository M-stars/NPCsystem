using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>Caches optional parameters until an Animator's controller changes.</summary>
    public static class SocialAnimatorParameters
    {
        private sealed class Binding
        {
            public RuntimeAnimatorController Controller;
            public readonly Dictionary<int, AnimatorControllerParameterType> Types =
                new Dictionary<int, AnimatorControllerParameterType>();
        }

        private static readonly ConditionalWeakTable<Animator, Binding> Bindings =
            new ConditionalWeakTable<Animator, Binding>();

        private static bool Has(Animator animator, int hash, AnimatorControllerParameterType type)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return false;
            var binding = Bindings.GetOrCreateValue(animator);
            if (binding.Controller != animator.runtimeAnimatorController)
            {
                binding.Controller = animator.runtimeAnimatorController;
                binding.Types.Clear();
                foreach (var parameter in animator.parameters)
                    binding.Types[parameter.nameHash] = parameter.type;
            }
            return binding.Types.TryGetValue(hash, out var actual) && actual == type;
        }

        public static void SetBool(Animator animator, int hash, bool value)
        {
            if (Has(animator, hash, AnimatorControllerParameterType.Bool)) animator.SetBool(hash, value);
        }

        public static void SetInteger(Animator animator, int hash, int value)
        {
            if (Has(animator, hash, AnimatorControllerParameterType.Int)) animator.SetInteger(hash, value);
        }
    }
}
