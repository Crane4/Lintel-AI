using System.Text;

namespace LintelAI.Services;

public sealed record MessageBlock(bool IsCode, string Text, string? Language);

/// <summary>Splits a chat reply into prose and fenced code so the two can be styled differently.</summary>
public static class MessageParser
{
    public static IReadOnlyList<MessageBlock> Split(string text)
    {
        var blocks = new List<MessageBlock>();
        var buffer = new StringBuilder();
        var inCode = false;
        string? language = null;

        void Flush()
        {
            var value = buffer.ToString().Trim('\n');
            buffer.Clear();
            if (inCode) blocks.Add(new MessageBlock(true, value.TrimEnd(), language));
            else if (value.Trim().Length > 0) blocks.Add(new MessageBlock(false, value.TrimEnd(), null));
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                Flush();
                if (!inCode) language = trimmed.Length > 3 ? trimmed[3..].Trim() : null;
                inCode = !inCode;
                continue;
            }

            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(line);
        }

        // An unterminated fence is normal while a reply is still streaming.
        if (buffer.Length > 0 || inCode) Flush();
        return blocks;
    }
}
