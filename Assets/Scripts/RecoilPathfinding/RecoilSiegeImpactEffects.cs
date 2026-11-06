using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [SerializeField] private GameObject siegeImpactPrefab;
        private const int MaximumSiegeImpactEffects = 32;
        private readonly List<SiegeImpactEffect> _siegeImpactEffects = new();
        private int _nextSiegeImpactEffect;
        private bool _siegeImpactDefaultsLoaded;

        private sealed class SiegeImpactEffect
        {
            public GameObject Object;
            public ParticleSystem[] Particles;
        }

        private void PlaySiegeImpact(PendingCombatProjectile projectile, Vector3 position)
        {
            if (!projectile.SiegeStone) return;
            if (siegeImpactPrefab == null && !_siegeImpactDefaultsLoaded)
            {
                _siegeImpactDefaultsLoaded = true;
                var defaults = Resources.Load<RecoilBattleEffects>("RecoilBattleEffects");
                if (defaults != null) siegeImpactPrefab = defaults.SiegeImpactPrefab;
            }
            if (siegeImpactPrefab == null) return;
            SiegeImpactEffect effect = null;
            foreach (var candidate in _siegeImpactEffects)
                if (candidate.Object != null && !candidate.Object.activeSelf) { effect = candidate; break; }
            if (effect == null && _siegeImpactEffects.Count < MaximumSiegeImpactEffects)
            {
                var instance = Instantiate(siegeImpactPrefab, transform);
                instance.SetActive(false);
                effect = new SiegeImpactEffect { Object = instance, Particles = instance.GetComponentsInChildren<ParticleSystem>(true) };
                foreach (var particle in effect.Particles)
                {
                    var main = particle.main;
                    main.loop = false;
                    main.stopAction = ParticleSystemStopAction.None;
                }
                _siegeImpactEffects.Add(effect);
            }
            if (effect == null)
            {
                effect = _siegeImpactEffects[_nextSiegeImpactEffect];
                _nextSiegeImpactEffect = (_nextSiegeImpactEffect + 1) % MaximumSiegeImpactEffects;
            }
            if (effect.Object == null) return;
            foreach (var particle in effect.Particles) particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            effect.Object.transform.SetPositionAndRotation(position, siegeImpactPrefab.transform.rotation);
            effect.Object.SetActive(true);
            foreach (var particle in effect.Particles) particle.Play(false);
        }

        private void UpdateSiegeImpactEffects()
        {
            foreach (var effect in _siegeImpactEffects)
            {
                if (effect.Object == null || !effect.Object.activeSelf) continue;
                var alive = false;
                foreach (var particle in effect.Particles)
                    if (particle.IsAlive(false)) { alive = true; break; }
                if (!alive) effect.Object.SetActive(false);
            }
        }

        private void ReleaseSiegeImpactEffects()
        {
            foreach (var effect in _siegeImpactEffects)
                if (effect.Object != null) Destroy(effect.Object);
            _siegeImpactEffects.Clear();
            _nextSiegeImpactEffect = 0;
        }
    }
}
