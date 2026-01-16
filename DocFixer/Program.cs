using YamlDotNet.Serialization;

var manifestFolder = new DirectoryInfo(Path.Join(Environment.CurrentDirectory, "api"));

if (!manifestFolder.Exists)
    return -1;

var deserializer = new DeserializerBuilder().Build();
var serializer = new SerializerBuilder().Build();
var manifests = manifestFolder.GetFiles("*.yml");
var derivedClassesOnFiles = new Dictionary<string, HashSet<string>>();
Console.WriteLine("Fixing Inheritance tree");
foreach (var manifest in manifests)
{
    var reader = manifest.OpenText();
    var data = deserializer.Deserialize(reader);
    reader.Dispose();
    if (data?.GetType() != typeof(Dictionary<object, object>))
    {
        Console.Error.WriteLine($"{manifest.FullName} not in expected format");
        continue;
    }
    var dataDict = (Dictionary<object, object>)data;
    var items = (List<object>)dataDict["items"];
    var self = (Dictionary<object, object>)items[0];
    var inheritance = (self.TryGetValue("inheritance", out var inh) ? (List<object>)inh : []).ToHashSet();
    if (self.TryGetValue("attributes", out var attributes))
    {
        var attributeList = (List<object>)attributes;
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var attribute in attributeList)
        {
            var attributeDict = (Dictionary<object, object>)attribute;
            var attrType = (string)attributeDict["type"];
            if (!attrType.Contains("InheritsAttribute"))
                continue;
            var inheritanceString = attrType.Split('{')[1][..^1];
            inheritance.Add(inheritanceString);
            if (!derivedClassesOnFiles.ContainsKey(inheritanceString))
                derivedClassesOnFiles[inheritanceString] = [];
            derivedClassesOnFiles[inheritanceString].Add((string)self["uid"]);
        }
    }
    if (inheritance.Count == 0)
        continue;
    self["inheritance"] = inheritance.ToArray();
    
    manifest.Delete();
    var writer = manifest.OpenWrite();
    var streamWriter = new StreamWriter(writer);
    streamWriter.WriteLine("### YamlMime:ManagedReference");
    streamWriter.Write(serializer.Serialize(data));
    streamWriter.Flush();
    writer.Flush();
    streamWriter.Dispose();
    writer.Dispose();
}
Console.WriteLine("Fixing Derived tree");
foreach (var manifest in manifests)
{
    var reader = manifest.OpenText();
    var data = deserializer.Deserialize(reader);
    reader.Dispose();
    if (data?.GetType() != typeof(Dictionary<object, object>))
    {
        Console.Error.WriteLine($"{manifest.FullName} not in expected format");
        continue;
    }
    var dataDict = (Dictionary<object, object>)data;
    var items = (List<object>)dataDict["items"];
    var self = (Dictionary<object, object>)items[0];
    var uid = (string)self["uid"];
    if(!derivedClassesOnFiles.TryGetValue(uid, out var derivedClasses))
        continue;
    self["derivedClasses"] = derivedClasses.ToArray();

    manifest.Delete();
    var writer = manifest.OpenWrite();
    var streamWriter = new StreamWriter(writer);
    streamWriter.WriteLine("### YamlMime:ManagedReference");
    streamWriter.Write(serializer.Serialize(data));
    streamWriter.Flush();
    writer.Flush();
    streamWriter.Dispose();
    writer.Dispose();
}

return 0;

