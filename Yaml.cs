using System.Text.RegularExpressions;
using SharpYaml.Serialization;
using SharpYaml.Serialization.Serializers;
using Tomlyn.Model;

namespace K8sDeploy;

public class YamlTemplateSerializer
{
    private TomlTable _values;
    private TomlTable _secrets;

    private Serializer _serializer;
    private Serializer _secretSerializer;

    public YamlTemplateSerializer(TomlTable values, TomlTable secrets)
    {
        _values = values;
        _secrets = secrets;

        _serializer = new SharpYaml.Serialization.Serializer(new SharpYaml.Serialization.SerializerSettings
        {
            ObjectSerializerBackend = new YamlTemplateNodeSerializer(values, null),
            LimitPrimitiveFlowSequence = 2, // Avoid issue with empty string values in arrays
        });

        _secretSerializer = new SharpYaml.Serialization.Serializer(new SharpYaml.Serialization.SerializerSettings
        {
            ObjectSerializerBackend = new YamlTemplateNodeSerializer(values, secrets),
            LimitPrimitiveFlowSequence = 2, // Avoid issue with empty string values in arrays
        });
    }

    public async Task TransformYamlTemplate(string filePath, string tmpFilePath, bool isSecret)
    {
        var serializer = isSecret ? _secretSerializer : _serializer;
        var content = await File.ReadAllTextAsync(filePath);
        content = new Regex(@"\r\n", RegexOptions.Multiline).Replace(content, "\n");
        var yamls = new Regex(@"\n---\n", RegexOptions.Multiline).Split(content);
        var newYamls = new List<string>();
        foreach (var yaml in yamls)
        {
            var doc = serializer.Deserialize<object>(yaml);
            var newYaml = serializer.Serialize(doc);
            if (newYaml.StartsWith('!'))
                newYaml = newYaml.Substring(newYaml.IndexOf('\n'));
            newYamls.Add(newYaml.Trim());
        }
        await File.WriteAllTextAsync(Path.ChangeExtension(tmpFilePath, ".yaml"), string.Join("\n---\n", newYamls));
    }
}

public class YamlTemplateException : Exception
{
    public YamlTemplateException(string? message) : base(message)
    {
    }
}

public class YamlTemplateNodeSerializer : DefaultObjectSerializerBackend
{
    private TomlTable _values;
    private TomlTable? _secrets;

    private Regex _rx = new Regex(@"\$\{\{\s*(.*?)\s*\}\}", RegexOptions.Compiled);

    public YamlTemplateNodeSerializer(TomlTable values, TomlTable? secrets)
    {
        _values = values;
        _secrets = secrets;
    }

    public override object? ReadMemberValue(ref ObjectContext objectContext, IMemberDescriptor? memberDescriptor, object? memberValue, Type memberType)
    {
        return TransformValue(base.ReadMemberValue(ref objectContext, memberDescriptor, memberValue, memberType));
    }

    public override object? ReadCollectionItem(ref ObjectContext objectContext, object? value, Type itemType, int index)
    {
        return TransformValue(base.ReadCollectionItem(ref objectContext, value, itemType, index));
    }

    public override KeyValuePair<object, object?> ReadDictionaryItem(ref ObjectContext objectContext, KeyValuePair<Type, Type> keyValueType)
    {
        var keyResult = objectContext.SerializerContext.ReadYaml(null, keyValueType.Key);
        var valueResult = TransformValue(objectContext.SerializerContext.ReadYaml(null, keyValueType.Value));

        return new KeyValuePair<object, object?>(keyResult!, valueResult);
    }

    protected object? TransformValue(object? obj)
    {
        if (obj is string t) return TransformString(t);
        return obj;
    }

    protected string TransformString(string template)
    {
        return _rx.Replace(template, (match) =>
        {
            var key = match.Groups[1].Value;
            if (TryGetValue(key, out var value))
            {
                return value?.ToString() ?? "";
            }
            throw new YamlTemplateException($"Yaml template refers unknown key {key}");
        });
    }

    private bool TryGetValue(string key, out object? value)
    {
        var hierarchy = key.Split('.');
        TomlTable values = _values;

        if (hierarchy[0] == "secrets")
        {
            if (_secrets == null)
                throw new YamlTemplateException($"Secret values can only be used in yaml files located in the @secrets directory. Tried to reference {key}.");

            values = _secrets;
            hierarchy = hierarchy.Skip(1).ToArray();
        }

        object? read = null;
        foreach (var k in hierarchy)
        {
            if (values.TryGetValue(k, out read))
            {
                if (read is TomlTable t)
                    values = t;
            }
            else
            {
                value = null;
                return false;
            }
        }
        value = read;
        return read != null;
    }
}
