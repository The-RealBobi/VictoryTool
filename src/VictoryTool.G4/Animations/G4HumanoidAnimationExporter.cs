using System.Numerics;
using System.Text;

namespace VictoryTool.G4.Animations;

public static class G4HumanoidAnimationExporter
{
    public static byte[] Export(G4mtAnimationBank bank, G4AvatarRecipe recipe, CancellationToken cancellationToken)
    {
        recipe.Validate();
        var rig = new PreparedRig(recipe);
        if (bank.Clips.All(clip => (clip.Flags & 1) != 0)) return bank.Source.ToArray();
        IEnumerable<G4mtClipSamples> Samples()
        {
            foreach (var clip in bank.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((clip.Flags & 1) != 0) continue;
                yield return SampleClip(bank, clip, rig, cancellationToken);
            }
        }
        return G4mtAnimationFamilyWriter.WritePreservingAdditiveStreaming(bank, Samples(), cancellationToken);
    }

    public static G4mtClipSamples SampleClip(G4mtAnimationBank bank, G4mtClip clip,
        G4AvatarRecipe recipe, CancellationToken cancellationToken)
    {
        recipe.Validate();
        if ((clip.Flags & 1) != 0) throw new InvalidDataException("Additive clips retain their native payload.");
        return SampleClip(bank, clip, new PreparedRig(recipe), cancellationToken);
    }

    private static G4mtClipSamples SampleClip(G4mtAnimationBank bank, G4mtClip clip,
        PreparedRig rig, CancellationToken cancellationToken)
    {
        var infos = bank.TargetInfos.Skip(clip.TargetInfoStart).Take(clip.TargetInfoCount)
            .ToDictionary(info => bank.TargetHashes[info.TargetIndex]);
        var sourceInfos = rig.Source.Select(node => infos.GetValueOrDefault(Crc32(node.Name))).ToArray();
        if (!sourceInfos.Any(info => info is not null && info.ChannelCount > 0))
            throw new InvalidDataException($"Animation clip '{clip.Name}' has no tracks for its source rig.");
        var frames = clip.EndFrame - clip.StartFrame + 1;
        var samples = rig.Target.Select(_ => new List<G4mtJointRest>(frames)).ToArray();
        var sourceLocals = new Matrix4x4[rig.Source.Length];
        var sourceGlobals = new Matrix4x4[rig.Source.Length];
        var sourceRaw = new G4mtJointRest[rig.Source.Length];
        var desired = new Matrix4x4[rig.Target.Length];
        var alignment = rig.Alignment;
        var inverseAlignment = Quaternion.Inverse(alignment);
        for (var frame = (int)clip.StartFrame; frame <= clip.EndFrame; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < rig.Source.Length; index++)
                sourceRaw[index] = ReadTrack(bank, sourceInfos[index], rig.Source[index].TrackRest!.ToRest(), frame);
            for (var index = 0; index < rig.Source.Length; index++)
            {
                var node = rig.Source[index];
                var info = sourceInfos[index];
                var local = rig.SourceLocals[index];
                if (info is not null && info.ChannelCount > 0)
                {
                    var raw = sourceRaw[index];
                    var parent = rig.SourceParents[index];
                    if (parent >= 0 && sourceInfos[parent] is not null)
                        raw = raw with { Scale = ParentScaleRatio(raw.Scale, sourceRaw[parent].Scale) };
                    var correction = rig.SourceCorrections[index];
                    var delta = correction * ToMatrix(raw) * rig.SourceTrackRestInverse[index] * rig.SourceCorrectionInverses[index];
                    var basis = Decompose(delta);
                    var channels = bank.Channels.Skip(info.ChannelStart).Take(info.ChannelCount).Select(c => c.Type).ToHashSet();
                    basis = basis with
                    {
                        Translation = channels.Any(type => type is 10 or 11 or 12) ? basis.Translation : Vector3.Zero,
                        Rotation = channels.Contains(9) ? basis.Rotation : Quaternion.Identity,
                        Scale = channels.Any(type => type is 1 or 2 or 3) ? basis.Scale : Vector3.One,
                    };
                    local = ToMatrix(basis) * local;
                }
                sourceLocals[index] = local;
                sourceGlobals[index] = rig.SourceParents[index] >= 0
                    ? local * sourceGlobals[rig.SourceParents[index]] : local;
            }
            var rootDisplacement = rig.SourceRoot >= 0
                ? sourceGlobals[rig.SourceRoot].Translation - rig.SourceRests[rig.SourceRoot].Translation : Vector3.Zero;
            foreach (var index in rig.Order)
            {
                var parent = rig.TargetParents[index];
                var basis = parent >= 0 ? rig.TargetLocals[index] * desired[parent] : rig.TargetLocals[index];
                if (rig.MappedSources.TryGetValue(index, out var sourceIndex))
                {
                    var sourcePose = sourceGlobals[sourceIndex];
                    var delta = Q(sourcePose) * Quaternion.Inverse(Q(rig.SourceRests[sourceIndex]));
                    var rotation = Quaternion.Normalize(alignment * delta * inverseAlignment * rig.ReferenceRotations[index]);
                    var position = basis.Translation;
                    var role = rig.TargetRoles[index];
                    if (role == "c_global_0_0")
                        position = rig.TargetRests[index].Translation + rootDisplacement * rig.Recipe.BodyScale;
                    else
                    {
                        var sourceParent = rig.SourceParents[sourceIndex];
                        var parentPose = sourceParent >= 0 ? sourceGlobals[sourceParent] : Matrix4x4.Identity;
                        var expected = rig.SourceLocals[sourceIndex] * parentPose;
                        var displacement = sourcePose.Translation - expected.Translation;
                        var proportion = role == "c_c_1_0" ? rig.Recipe.HipScale : rig.Recipe.SegmentScales[rig.Target[index].Name];
                        position += Vector3.Transform(displacement, alignment) * proportion;
                        if (role == "c_c_1_0" && rig.TargetRoot < 0)
                            position += Vector3.Transform(rootDisplacement, alignment) * rig.Recipe.BodyScale;
                    }
                    basis = ToMatrix(new(position, rotation, Decompose(basis).Scale));
                }
                else if (rig.Twists.TryGetValue(index, out var twist))
                {
                    var driverRest = rig.TargetRests[twist.Driver];
                    var relativeRest = rig.TargetRests[index] * Inverse(driverRest);
                    var sourceEnd = rig.MappedSources[twist.End];
                    var endDelta = Q(sourceGlobals[sourceEnd]) * Quaternion.Inverse(Q(rig.SourceRests[sourceEnd]));
                    var endRotation = alignment * endDelta * inverseAlignment * rig.ReferenceRotations[twist.End];
                    var relativeRotation = Quaternion.Inverse(Q(desired[twist.Driver])) * endRotation;
                    var restRotation = Quaternion.Inverse(Q(driverRest)) * Q(rig.TargetRests[twist.End]);
                    var delta = relativeRotation * Quaternion.Inverse(restRotation);
                    var axis = Vector3.Normalize(Vector3.TransformNormal(
                        rig.TargetRests[twist.End].Translation - driverRest.Translation, Inverse(driverRest)));
                    var vector = new Vector3(delta.X, delta.Y, delta.Z);
                    var projection = axis * Vector3.Dot(axis, vector);
                    var rotation = new Quaternion(projection, delta.W);
                    rotation = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
                    if (rotation.W < 0) rotation = Negate(rotation);
                    var distributed = Quaternion.Slerp(Quaternion.Identity, rotation, twist.Fraction);
                    basis = relativeRest * Matrix4x4.CreateFromQuaternion(distributed) * desired[twist.Driver];
                }
                desired[index] = basis;
            }
            for (var index = 0; index < rig.Target.Length; index++)
            {
                var parent = rig.TargetParents[index];
                var local = parent >= 0 ? desired[index] * Inverse(desired[parent]) : desired[index];
                if (rig.PreservedSources.TryGetValue(index, out var preservedIndex))
                    local = sourceLocals[preservedIndex];
                var rest = Decompose(local);
                var previous = samples[index].LastOrDefault();
                if (previous is not null && Quaternion.Dot(previous.Rotation, rest.Rotation) < 0)
                    rest = rest with { Rotation = Negate(rest.Rotation) };
                samples[index].Add(rest);
            }
        }
        return new G4mtClipSamples(clip.Index, clip.Name, rig.Target.Select((node, index) =>
            new G4mtTargetSamples(node.Name, rig.TargetParents[index] >= 0 ? rig.TargetParents[index] : null,
                samples[index])).ToArray());
    }

    private static G4mtJointRest ReadTrack(G4mtAnimationBank bank, G4mtTargetInfo? info,
        G4mtJointRest rest, int frame)
    {
        if (info is null) return rest;
        var translation = rest.Translation; var rotation = rest.Rotation; var scale = rest.Scale;
        for (var offset = 0; offset < info.ChannelCount; offset++)
        {
            var channel = bank.Channels[info.ChannelStart + offset];
            if (channel.Type is not (1 or 2 or 3 or 9 or 10 or 11 or 12) || channel.KeyCount == 0) continue;
            var value = bank.SampleChannel(channel.Index, frame);
            switch (channel.Type)
            {
                case 1: scale.X = value[0]; break;
                case 2: scale.Y = value[0]; break;
                case 3: scale.Z = value[0]; break;
                case 9:
                    rotation = new(value[0], value[1], value[2], value[3]);
                    rotation = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
                    break;
                case 10: translation.X = value[0]; break;
                case 11: translation.Y = value[0]; break;
                case 12: translation.Z = value[0]; break;
            }
        }
        return new(translation, rotation, scale);
    }

    private static Vector3 ParentScaleRatio(Vector3 scale, Vector3 parent) => new(
        MathF.Abs(parent.X) > 1e-9f ? scale.X / parent.X : scale.X,
        MathF.Abs(parent.Y) > 1e-9f ? scale.Y / parent.Y : scale.Y,
        MathF.Abs(parent.Z) > 1e-9f ? scale.Z / parent.Z : scale.Z);
    private static Matrix4x4 ToMatrix(G4mtJointRest rest) => Matrix4x4.CreateScale(rest.Scale)
        * Matrix4x4.CreateFromQuaternion(rest.Rotation) * Matrix4x4.CreateTranslation(rest.Translation);
    private static Matrix4x4 ReadMatrix(float[] v) => Matrix4x4.Transpose(new(
        v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]));
    private static Matrix4x4 Inverse(Matrix4x4 matrix) => Matrix4x4.Invert(matrix, out var inverse)
        ? inverse : throw new InvalidDataException("An animation rig transform is singular.");
    private static G4mtJointRest Decompose(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation)
            || !float.IsFinite(scale.X + scale.Y + scale.Z + rotation.LengthSquared() + translation.LengthSquared()))
            throw new InvalidDataException("An animation pose cannot be represented as a finite local transform.");
        return new(translation, Quaternion.Normalize(rotation), scale);
    }
    private static Quaternion Q(Matrix4x4 matrix) => Decompose(matrix).Rotation;
    private static Quaternion Negate(Quaternion q) => new(-q.X, -q.Y, -q.Z, -q.W);
    private static uint Crc32(string value)
    {
        var crc = uint.MaxValue;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    private sealed class PreparedRig
    {
        public PreparedRig(G4AvatarRecipe recipe)
        {
            Recipe = recipe; Source = recipe.SourceNodes.ToArray(); Target = recipe.TargetNodes.ToArray();
            var sourceIndices = Source.Select((n, i) => (n.Name, i)).ToDictionary(p => p.Name, p => p.i);
            var targetIndices = Target.Select((n, i) => (n.Name, i)).ToDictionary(p => p.Name, p => p.i);
            SourceParents = Source.Select(n => n.Parent is null ? -1 : sourceIndices[n.Parent]).ToArray();
            TargetParents = Target.Select(n => n.Parent is null ? -1 : targetIndices[n.Parent]).ToArray();
            SourceRests = Source.Select(n => ReadMatrix(n.RestMatrix)).ToArray();
            TargetRests = Target.Select(n => ReadMatrix(n.RestMatrix)).ToArray();
            SourceLocals = Source.Select(n => ToMatrix(n.LocalRest.ToRest())).ToArray();
            TargetLocals = Target.Select(n => ToMatrix(n.LocalRest.ToRest())).ToArray();
            SourceTrackRestInverse = Source.Select(n => Inverse(ToMatrix(n.TrackRest!.ToRest()))).ToArray();
            SourceCorrections = SourceLocals.Select((local, i) =>
            {
                var correction = local * SourceTrackRestInverse[i];
                correction.Translation = Vector3.Zero; return correction;
            }).ToArray();
            SourceCorrectionInverses = SourceCorrections.Select(Inverse).ToArray();
            Alignment = Quaternion.Normalize(new(recipe.Alignment[0], recipe.Alignment[1], recipe.Alignment[2], recipe.Alignment[3]));
            foreach (var (role, target) in recipe.TargetMapping)
            {
                var index = targetIndices[target];
                MappedSources[index] = sourceIndices[recipe.SourceMapping[role]];
                TargetRoles[index] = role;
                var rotation = recipe.ReferenceRotations[target];
                ReferenceRotations[index] = Quaternion.Normalize(new(rotation[0], rotation[1], rotation[2], rotation[3]));
            }
            SourceRoot = recipe.SourceMapping.TryGetValue("c_global_0_0", out var root) ? sourceIndices[root] : -1;
            TargetRoot = recipe.TargetMapping.TryGetValue("c_global_0_0", out root) ? targetIndices[root] : -1;
            foreach (var twist in recipe.Twists)
                Twists[targetIndices[twist.Bone]] = (targetIndices[twist.Driver], targetIndices[twist.End], twist.Fraction);
            foreach (var name in recipe.PreserveLocalTracks) PreservedSources[targetIndices[name]] = sourceIndices[name];
            var order = new List<int>(); var pending = new HashSet<int>(Enumerable.Range(0, Target.Length));
            var resolved = new HashSet<int>();
            while (pending.Count > 0)
            {
                var ready = pending.Where(i => (TargetParents[i] < 0 || resolved.Contains(TargetParents[i]))
                    && (!Twists.TryGetValue(i, out var t) || resolved.Contains(t.Driver))).Order().ToArray();
                if (ready.Length == 0) throw new InvalidDataException("Twist assignments conflict with the rig hierarchy.");
                foreach (var i in ready) { pending.Remove(i); resolved.Add(i); order.Add(i); }
            }
            Order = order.ToArray();
        }
        public G4AvatarRecipe Recipe { get; }
        public G4AvatarNode[] Source { get; }
        public G4AvatarNode[] Target { get; }
        public int[] SourceParents { get; }
        public int[] TargetParents { get; }
        public Matrix4x4[] SourceRests { get; }
        public Matrix4x4[] TargetRests { get; }
        public Matrix4x4[] SourceLocals { get; }
        public Matrix4x4[] TargetLocals { get; }
        public Matrix4x4[] SourceTrackRestInverse { get; }
        public Matrix4x4[] SourceCorrections { get; }
        public Matrix4x4[] SourceCorrectionInverses { get; }
        public Quaternion Alignment { get; }
        public Dictionary<int, int> MappedSources { get; } = [];
        public Dictionary<int, string> TargetRoles { get; } = [];
        public Dictionary<int, Quaternion> ReferenceRotations { get; } = [];
        public Dictionary<int, (int Driver, int End, float Fraction)> Twists { get; } = [];
        public Dictionary<int, int> PreservedSources { get; } = [];
        public int SourceRoot { get; }
        public int TargetRoot { get; }
        public int[] Order { get; }
    }
}
