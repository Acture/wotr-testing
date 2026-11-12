using System;
using System.Globalization;
using Kingmaker.Settings;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The game's difficulty settings for a battle (<see cref="BattleScenario.Difficulty"/>, <see cref="BattleScenario.LimitedAI"/>),
    /// set as a player sets them in the game's options and restored at cleanup.
    /// </summary>
    internal sealed class BattleDifficulty
    {
        private readonly GameDifficultyOption gameDifficulty;
        private readonly bool deathDoor;
        private readonly bool deadCompanionsRise;
        private readonly bool removeNegativeEffects;
        private readonly bool removeAnnoyingBuffs;
        private readonly float damageToParty;
        private readonly CriticalHitPower enemyCriticalHits;
        private readonly StatsAdjustmentsType statsAdjustments;
        private readonly EnemyDifficulty enemyDifficulty;
        private readonly bool encumbrance;
        private readonly WeatherEffects weather;
        private readonly bool respec;
        private readonly AutoLevelUpOption autoLevelUp;
        private readonly bool additionalAi;
        private readonly CombatEncountersCapacity encounters;
        private readonly bool limitedAi;

        private BattleDifficulty()
        {
            var d = SettingsRoot.Difficulty;
            gameDifficulty = d.GameDifficulty.GetValue();
            deathDoor = d.DeathDoor.GetValue();
            deadCompanionsRise = d.DeadCompanionsRiseAfterCombat.GetValue();
            removeNegativeEffects = d.RemoveNegativeEffectsOnRest.GetValue();
            removeAnnoyingBuffs = d.RemoveAnnoyingBuffsAfterCombat.GetValue();
            damageToParty = d.DamageToParty.GetValue();
            enemyCriticalHits = d.EnemyCriticalHits.GetValue();
            statsAdjustments = d.StatsAdjustments.GetValue();
            enemyDifficulty = d.EnemyDifficulty.GetValue();
            encumbrance = d.EncumbranceSlowdown.GetValue();
            weather = d.WeatherEffects.GetValue();
            respec = d.RespecAllowed.GetValue();
            autoLevelUp = d.AutoLevelUp.GetValue();
            additionalAi = d.AdditionalAIBehaviors.GetValue();
            encounters = d.CombatEncountersCapacity.GetValue();
            limitedAi = d.LimitedAI.GetValue();
        }

        /// <summary>The settings now, to restore at cleanup.</summary>
        internal static BattleDifficulty Capture() => new BattleDifficulty();

        /// <summary>The difficulty settings the battle rules read, for state comparisons and reports.</summary>
        internal static string Describe() => Capture().ToString();

        /// <summary>
        /// Chooses a difficulty level as a player does in the game's options: the game's DifficultyPresetsController fills
        /// in that level's preset when the level changes, and the values are confirmed.
        /// </summary>
        internal static void Choose(GameDifficultyOption level)
        {
            if (level == GameDifficultyOption.Custom)
                throw new ArgumentException("Choose a difficulty level; Custom is what the game shows after individual settings are changed.", nameof(level));
            SettingsRoot.Difficulty.GameDifficulty.SetTempValue(level);
            SettingsController.ConfirmAllTempValues();
            if (SettingsRoot.Difficulty.GameDifficulty.GetValue() != level)
                throw new InvalidOperationException($"The game's difficulty is {SettingsRoot.Difficulty.GameDifficulty.GetValue()} after choosing {level}.");
        }

        internal static void SetLimitedAI(bool value) => SettingsRoot.Difficulty.LimitedAI.SetValueAndConfirm(value);

        /// <summary>
        /// Puts every setting back: the level first, which lets the game fill in its preset, then each value, so a custom
        /// combination comes back as it was.
        /// </summary>
        internal void Restore()
        {
            var d = SettingsRoot.Difficulty;
            if (gameDifficulty != GameDifficultyOption.Custom) Choose(gameDifficulty);
            d.DeathDoor.SetValueAndConfirm(deathDoor);
            d.DeadCompanionsRiseAfterCombat.SetValueAndConfirm(deadCompanionsRise);
            d.RemoveNegativeEffectsOnRest.SetValueAndConfirm(removeNegativeEffects);
            d.RemoveAnnoyingBuffsAfterCombat.SetValueAndConfirm(removeAnnoyingBuffs);
            d.DamageToParty.SetValueAndConfirm(damageToParty);
            d.EnemyCriticalHits.SetValueAndConfirm(enemyCriticalHits);
            d.StatsAdjustments.SetValueAndConfirm(statsAdjustments);
            d.EnemyDifficulty.SetValueAndConfirm(enemyDifficulty);
            d.EncumbranceSlowdown.SetValueAndConfirm(encumbrance);
            d.WeatherEffects.SetValueAndConfirm(weather);
            d.RespecAllowed.SetValueAndConfirm(respec);
            d.AutoLevelUp.SetValueAndConfirm(autoLevelUp);
            d.AdditionalAIBehaviors.SetValueAndConfirm(additionalAi);
            d.CombatEncountersCapacity.SetValueAndConfirm(encounters);
            d.LimitedAI.SetValueAndConfirm(limitedAi);
            d.GameDifficulty.SetValueAndConfirm(gameDifficulty);
            var now = Describe();
            if (now != ToString())
                throw new InvalidOperationException($"The difficulty settings are \"{now}\" after restoring \"{this}\".");
        }

        public override string ToString()
            => string.Join(" ",
                "level " + gameDifficulty,
                "damage-to-party " + damageToParty.ToString("R", CultureInfo.InvariantCulture),
                "enemy " + enemyDifficulty,
                "enemy-crits " + enemyCriticalHits,
                "stats " + statsAdjustments,
                "additional-ai " + additionalAi,
                "limited-ai " + limitedAi,
                "death-door " + deathDoor,
                "dead-rise " + deadCompanionsRise,
                "rest-negative " + removeNegativeEffects,
                "annoying-buffs " + removeAnnoyingBuffs,
                "encumbrance " + encumbrance,
                "weather " + weather,
                "respec " + respec,
                "auto-level " + autoLevelUp,
                "encounters " + encounters);
    }
}
