using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using VictoryTool.Application.Characters;
using VictoryTool.G4.Packages;
using VictoryTool.G4.Animations;

namespace VictoryTool.Application.Exporting;

internal static class AvatarAnimationTargetNames
{
    internal static (ExportPlan Plan, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> Names) Prepare(ExportPlan plan)
    {
        var requests = plan.AnimationGenerationRequests.Where(request => request.Profile.AvatarRecipe is not null).ToArray();
        var recipes = requests.ToDictionary(request => request.BatchEntryId,
            request => request.EventRecipe ?? request.Profile.AvatarRecipe!);
        var keys = recipes.ToDictionary(pair => pair.Key,
            pair => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(pair.Value))));
        var custom = recipes.ToDictionary(pair => pair.Key, pair => pair.Value.TargetNodes.Select(node => node.Name)
            .Except(pair.Value.SourceNodes.Select(node => node.Name), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal));
        var affected = new HashSet<Guid>();
        foreach (var group in requests.SelectMany(request => request.Banks
                     .Where(bank => bank.IsEvent && !AvatarAnimationResources.IsNativeEventEffect(bank))
                     .Select(bank => (request.BatchEntryId, bank.ResourcePath)))
                     .GroupBy(item => item.ResourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Select(item => item.BatchEntryId).Distinct().ToArray();
            for (var first = 0; first < ids.Length; first++)
                for (var second = first + 1; second < ids.Length; second++)
                    if (keys[ids[first]] != keys[ids[second]] && custom[ids[first]].Overlaps(custom[ids[second]]))
                    { affected.Add(ids[first]); affected.Add(ids[second]); }
        }
        var names = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        foreach (var request in requests.Where(request => affected.Contains(request.BatchEntryId)))
        {
            var prefix = request.GeneratedFamilyName
                ?? throw new InvalidDataException("A conflicting EVENT rig has no generated character family identifier.");
            names.Add(request.BatchEntryId, custom[request.BatchEntryId]
                .ToDictionary(name => name, name => $"{prefix}__{name}", StringComparer.Ordinal));
        }
        var updated = plan.AnimationGenerationRequests.Select(request =>
        {
            if (!names.TryGetValue(request.BatchEntryId, out var map)) return request;
            string Rename(string name) => map.GetValueOrDefault(name, name);
            var recipe = RenameRecipe(request.Profile.AvatarRecipe!, map);
            var skeleton = Convert.FromBase64String(recipe.TargetSkeletonBase64);
            var profile = request.Profile with
            {
                AvatarRecipe = recipe,
                TargetRig = request.Profile.TargetRig with { Sha256 = Convert.ToHexString(SHA256.HashData(skeleton)) },
                Joints = request.Profile.Joints.Select(joint => joint with
                {
                    Name = Rename(joint.Name), NameCrc32 = CharacterMotionInfoT2bWriter.Crc32(Rename(joint.Name)),
                    Parent = joint.Parent is null ? null : Rename(joint.Parent),
                }).ToArray(),
            };
            return request with { Profile = profile,
                EventRecipe = request.EventRecipe is null ? null : RenameRecipe(request.EventRecipe, map) };
        }).ToArray();
        return (plan with { AnimationGenerationRequests = updated }, names);
    }

    private static G4AvatarRecipe RenameRecipe(G4AvatarRecipe recipe, IReadOnlyDictionary<string, string> map)
    {
        string Rename(string name) => map.GetValueOrDefault(name, name);
        var updated = recipe with
        {
            TargetSkeletonBase64 = Convert.ToBase64String(RenameResource(Convert.FromBase64String(recipe.TargetSkeletonBase64), map)),
            TargetNodes = recipe.TargetNodes.Select(node => node with
            { Name = Rename(node.Name), Parent = node.Parent is null ? null : Rename(node.Parent) }).ToArray(),
            TargetMapping = recipe.TargetMapping.ToDictionary(pair => pair.Key, pair => Rename(pair.Value)),
            ReferenceRotations = recipe.ReferenceRotations.ToDictionary(pair => Rename(pair.Key), pair => pair.Value),
            SegmentScales = recipe.SegmentScales.ToDictionary(pair => Rename(pair.Key), pair => pair.Value),
            Twists = recipe.Twists.Select(twist => twist with
            { Bone = Rename(twist.Bone), Driver = Rename(twist.Driver), End = Rename(twist.End) }).ToArray(),
        };
        updated.Validate();
        return updated;
    }

    internal static byte[] RenameResource(byte[] content, IReadOnlyDictionary<string, string> names)
    {
        if (content.AsSpan().StartsWith("G4SK"u8))
        {
            var skeleton = G4skSkeletonReader.Read(content);
            var nodes = skeleton.Nodes.Select(node => node with { Name = names.GetValueOrDefault(node.Name, node.Name) }).ToArray();
            if (nodes.Select(node => node.Name).SequenceEqual(skeleton.Nodes.Select(node => node.Name))) return content;
            var result = G4skSkeletonWriter.SerializeV104(skeleton, nodes);
            G4skSkeletonReader.Read(result);
            return result;
        }
        if (content.AsSpan().StartsWith("G4MD"u8))
        {
            if (content.Length < 0xA0 || U16(content, 6) != 104)
                throw new InvalidDataException("Custom bone renaming requires a G4MD v104 descriptor.");
            var count = U16(content, 0x24);
            var offset = checked(U16(content, 0x0A) * 4 + U16(content, 0x74) * 4);
            if (offset < 0xA0 || offset > content.Length - count * 4)
                throw new InvalidDataException("The G4MD joint-hash table is outside its resource.");
            var hashes = names.ToDictionary(pair => CharacterMotionInfoT2bWriter.Crc32(pair.Key),
                pair => CharacterMotionInfoT2bWriter.Crc32(pair.Value));
            var result = content.ToArray();
            for (var index = 0; index < count; index++)
            {
                var at = offset + index * 4;
                var original = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(at));
                if (hashes.TryGetValue(original, out var renamed))
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), renamed);
            }
            return result;
        }
        if (content.AsSpan().StartsWith("G4PK"u8))
        {
            var package = G4pkModelPackage.Read(content);
            var skeleton = package.Members.SingleOrDefault(member => member.Content.Span.StartsWith("G4SK"u8));
            var model = package.Members.SingleOrDefault(member => member.Content.Span.StartsWith("G4MD"u8));
            if (skeleton is not null && model is not null)
            {
                var stem = Path.GetFileNameWithoutExtension(model.Name);
                if (skeleton.Name != $"{stem}.g4sk" || model.Name != $"{stem}.g4md")
                    throw new NotSupportedException("Custom binding renaming requires matching skeleton/model member names in a G4PKM.");
                return package.ReplaceCharacterModel(stem,
                    RenameResource(skeleton.Content.ToArray(), names), RenameResource(model.Content.ToArray(), names));
            }
        }
        return content;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
}
