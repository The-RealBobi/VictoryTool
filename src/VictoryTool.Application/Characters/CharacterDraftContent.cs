using VictoryTool.G4.Animations;

namespace VictoryTool.Application.Characters;

public static class CharacterDraftContent
{
    public static bool Equivalent(CharacterDraft? left, CharacterDraft? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.SymbolicId == right.SymbolicId
            && left.SourceCharacterId == right.SourceCharacterId
            && left.DisplayName == right.DisplayName
            && FieldsEqual(left.Fields, right.Fields)
            && TextEqual(left.StandardPortraitPath, right.StandardPortraitPath)
            && TextEqual(left.UniformPortraitPath, right.UniformPortraitPath)
            && left.Origin == right.Origin
            && IdentityEqual(left.Identity, right.Identity)
            && left.Gameplay == right.Gameplay
            && ModelsEqual(left.Models, right.Models)
            && AssetsEqual(left.Assets, right.Assets)
            && LocalizationEqual(left.Localization, right.Localization)
            && AcquisitionEqual(left.Acquisition, right.Acquisition)
            && SkillsEqual(left.Skills, right.Skills)
            && SequenceEqual(left.Variants, right.Variants, VariantEqual)
            && SequenceEqual(left.AnimationProfiles, right.AnimationProfiles, ProfileEqual);
    }

    private static bool FieldsEqual(IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right)
    {
        if (ReferenceEquals(left, right)) return true;
        // Clearing a field can leave an empty legacy entry that was absent in the saved draft.
        foreach (var (key, value) in left)
            if (!EmptyField(key, value) && (!right.TryGetValue(key, out var other) || value != other))
                return false;
        foreach (var (key, value) in right)
            if (!EmptyField(key, value) && (!left.TryGetValue(key, out var other) || value != other))
                return false;
        return true;
    }

    private static bool EmptyField(string key, string? value) =>
        string.IsNullOrEmpty(value) || value == ":" && key.Length == "Skills.Slot1".Length
            && key.StartsWith("Skills.Slot", StringComparison.Ordinal) && key[^1] is >= '1' and <= '9';

    private static bool TextEqual(string? left, string? right) =>
        left == right || string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right);

    private static bool IdentityEqual(CharacterDraftIdentity? left, CharacterDraftIdentity? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && TextEqual(left.DisplayName, right.DisplayName) && TextEqual(left.LocalizedName, right.LocalizedName);

    private static bool ModelsEqual(CharacterDraftModels? left, CharacterDraftModels? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && TextEqual(left.HeadModelPath, right.HeadModelPath) && TextEqual(left.BodyModelPath, right.BodyModelPath)
            && TextEqual(left.SkinColorRgba, right.SkinColorRgba)
            && TextEqual(left.UniformModelPath, right.UniformModelPath) && TextEqual(left.ShoesModelPath, right.ShoesModelPath)
            && left.UniformModel == right.UniformModel && left.ShoesModel == right.ShoesModel
            && left.GloveModel == right.GloveModel && left.EquipmentColor == right.EquipmentColor
            && left.UniformCollarOpen == right.UniformCollarOpen && left.EquipmentFlag2 == right.EquipmentFlag2
            && left.ChestSize == right.ChestSize && left.ForceKit == right.ForceKit;

    private static bool AssetsEqual(CharacterDraftAssets? left, CharacterDraftAssets? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && TextEqual(left.StandardPortraitPath, right.StandardPortraitPath)
            && TextEqual(left.UniformPortraitPath, right.UniformPortraitPath)
            && left.UniformFallback == right.UniformFallback;

    private static bool AcquisitionEqual(CharacterDraftAcquisition? left, CharacterDraftAcquisition? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && TextEqual(left.Method, right.Method) && TextEqual(left.Source, right.Source);

    private static bool LocalizedTextEqual(CharacterDraftLocalizedText left, CharacterDraftLocalizedText right) =>
        ReferenceEquals(left, right) || TextEqual(left.LocalizedName, right.LocalizedName)
            && TextEqual(left.Description, right.Description) && TextEqual(left.RomanizedName, right.RomanizedName)
            && TextEqual(left.JapaneseName, right.JapaneseName) && TextEqual(left.ShortName, right.ShortName)
            && TextEqual(left.UpperName, right.UpperName);

    private static bool LocalizationEqual(CharacterDraftLocalization? left, CharacterDraftLocalization? right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left is not null && right is not null
            && TextEqual(left.LocalizedName, right.LocalizedName) && TextEqual(left.RomanizedName, right.RomanizedName)
            && (ReferenceEquals(left.LocaleValues, right.LocaleValues)
                || DictionaryEqual(left.Locales, right.Locales, LocalizedTextEqual));
    }

    private static bool SkillsEqual(CharacterDraftSkills? left, CharacterDraftSkills? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && SequenceEqual(left.Slots, right.Slots, ValueEqual);

    private static bool VariantEqual(CharacterDraftVariant left, CharacterDraftVariant right) =>
        ReferenceEquals(left, right) || left.SourceParameterId == right.SourceParameterId
            && left.Gameplay == right.Gameplay && SkillsEqual(left.Skills, right.Skills);

    private static bool ProfileEqual(CharacterAnimationProfile left, CharacterAnimationProfile right) =>
        ReferenceEquals(left, right) || left.SchemaVersion == right.SchemaVersion
            && left.Producer == right.Producer && left.ModelStem == right.ModelStem
            && left.SourceModelStem == right.SourceModelStem && left.ModelPath == right.ModelPath
            && left.SourceRig == right.SourceRig && left.TargetRig == right.TargetRig
            && left.SourceRigBinding == right.SourceRigBinding
            && SequenceEqual(left.Joints, right.Joints, JointEqual)
            && RecipeEqual(left.AvatarRecipe, right.AvatarRecipe)
            && SequenceEqual(left.GeneratedBanks, right.GeneratedBanks, ValueEqual)
            && SequenceEqual(left.GeneratedPackages, right.GeneratedPackages, ValueEqual);

    private static bool JointEqual(CharacterAnimationJoint left, CharacterAnimationJoint right) =>
        ReferenceEquals(left, right) || left.Name == right.Name && left.NameCrc32 == right.NameCrc32
            && left.Parent == right.Parent && left.SourceParent == right.SourceParent
            && left.SourceName == right.SourceName && left.SourceNameCrc32 == right.SourceNameCrc32
            && TransformEqual(left.SourceRest, right.SourceRest)
            && TransformEqual(left.TargetRest, right.TargetRest)
            && TransformEqual(left.ModelRest, right.ModelRest)
            && SequenceEqual(left.MotionBasisRotation, right.MotionBasisRotation, ValueEqual);

    private static bool TransformEqual(CharacterAnimationTransform? left, CharacterAnimationTransform? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && SequenceEqual(left.Translation, right.Translation, ValueEqual)
            && SequenceEqual(left.Rotation, right.Rotation, ValueEqual)
            && SequenceEqual(left.Scale, right.Scale, ValueEqual);

    private static bool RecipeEqual(G4AvatarRecipe? left, G4AvatarRecipe? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.Version == right.Version && left.Algorithm == right.Algorithm
            && left.ExportChr == right.ExportChr && left.ExportEvent == right.ExportEvent
            && left.ChrFamily == right.ChrFamily && left.EventSkeleton == right.EventSkeleton
            && left.SourceSkeletonBase64 == right.SourceSkeletonBase64
            && left.TargetSkeletonBase64 == right.TargetSkeletonBase64
            && left.BodyScale.Equals(right.BodyScale) && left.HipScale.Equals(right.HipScale)
            && left.SourceScalePolicy == right.SourceScalePolicy
            && left.TargetCumulativeScale == right.TargetCumulativeScale
            && SequenceEqual(left.SourceNodes, right.SourceNodes, NodeEqual)
            && SequenceEqual(left.TargetNodes, right.TargetNodes, NodeEqual)
            && DictionaryEqual(left.SourceMapping, right.SourceMapping, ValueEqual)
            && DictionaryEqual(left.TargetMapping, right.TargetMapping, ValueEqual)
            && SequenceEqual(left.Alignment, right.Alignment, ValueEqual)
            && DictionaryEqual(left.ReferenceRotations, right.ReferenceRotations, FloatArrayEqual)
            && DictionaryEqual(left.SegmentScales, right.SegmentScales, ValueEqual)
            && SequenceEqual(left.Twists, right.Twists, ValueEqual)
            && SequenceEqual(left.PreserveLocalTracks, right.PreserveLocalTracks, ValueEqual);
    }

    private static bool NodeEqual(G4AvatarNode left, G4AvatarNode right) =>
        ReferenceEquals(left, right) || left.Name == right.Name && left.Parent == right.Parent
            && FloatArrayEqual(left.RestMatrix, right.RestMatrix)
            && AvatarTransformEqual(left.LocalRest, right.LocalRest)
            && AvatarTransformEqual(left.TrackRest, right.TrackRest);

    private static bool AvatarTransformEqual(G4AvatarTransform? left, G4AvatarTransform? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && FloatArrayEqual(left.Translation, right.Translation)
            && FloatArrayEqual(left.Rotation, right.Rotation)
            && FloatArrayEqual(left.Scale, right.Scale);

    private static bool FloatArrayEqual(float[] left, float[] right) =>
        SequenceEqual(left, right, ValueEqual);

    private static bool ValueEqual<T>(T left, T right) => EqualityComparer<T>.Default.Equals(left, right);

    private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right, Func<T, T, bool> equal)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!equal(left[index], right[index])) return false;
        return true;
    }

    private static bool DictionaryEqual<T>(IReadOnlyDictionary<string, T> left,
        IReadOnlyDictionary<string, T> right, Func<T, T, bool> equal)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other) || !equal(value, other)) return false;
        return true;
    }
}
