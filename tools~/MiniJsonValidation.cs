using System;
using System.Collections.Generic;
using UnityMCP.Editor;

internal static class MiniJsonValidation
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Main()
    {
        string[] emptyInputs = { null, "", " ", "\t\r\n" };
        foreach (string input in emptyInputs)
            Require(MiniJson.Deserialize(input) == null, "Empty input must return null");

        string[] truncatedContainers = { "{", "[", "{\"value\":1", "[1" };
        foreach (string input in truncatedContainers)
            Require(MiniJson.Deserialize(input) == null, "Truncated container must return null");

        Require((int)MiniJson.Deserialize("42") == 42, "Number at EOF");
        Require((bool)MiniJson.Deserialize("true"), "Boolean at EOF");
        Require((string)MiniJson.Deserialize("\"hello\"") == "hello", "String at EOF");
        var value = (Dictionary<string, object>)MiniJson.Deserialize(" \t{\"value\": [1, true, null]}\r\n");
        var items = (List<object>)value["value"];
        Require(items.Count == 3 && (int)items[0] == 1 && (bool)items[1] && items[2] == null, "Nested value and whitespace");
        Require(MiniJson.Serialize(MiniJson.Deserialize(MiniJson.Serialize(value))) == MiniJson.Serialize(value), "Round trip");
        Console.WriteLine("PASS: 13 MiniJson EOF, whitespace, value and round-trip checks");
    }
}
