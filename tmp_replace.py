content = open('/copilot-home/SharpCoder/src/SharpCoder/CodingAgent.cs').read()

old = (
    '            if (streamError != null)\n'
    '            {\n'
    '                _logger.LogError(streamError, "Streaming agent execution failed.");\n'
    '                yield return StreamingUpdate.Completed(new AgentResult\n'
    '                {\n'
    '                    Status = "Error",\n'
    '                    Message = streamError.Message,\n'
    '                    Diagnostics = diagnostics,\n'
    '                });\n'
    '                yield break;\n'
    '            }\n'
    '\n'
    '            // Reconstruct the response from streaming updates'
)

new = (
    '            if (streamError != null)\n'
    '            {\n'
    '                if (ContextCompactor.IsContextOverflowError(streamError))\n'
    '                {\n'
    '                    _logger.LogWarning(streamError, "Context overflow \u2014 compacting and retrying");\n'
    '                    if (session != null && await _compactor.ForceCompactAsync(session, _options, ct))\n'
    '                    {\n'
    '                        messages = BuildMessages(session, ""); // rebuild from compacted session\n'
    '                        messages.RemoveAt(messages.Count - 1); // remove the empty user message\n'
    '                        continue; // retry the round\n'
    '                    }\n'
    '                }\n'
    '\n'
    '                _logger.LogError(streamError, "Streaming agent execution failed.");\n'
    '                yield return StreamingUpdate.Completed(new AgentResult\n'
    '                {\n'
    '                    Status = "Error",\n'
    '                    Message = streamError.Message,\n'
    '                    Diagnostics = diagnostics,\n'
    '                });\n'
    '                yield break;\n'
    '            }\n'
    '\n'
    '            // Reconstruct the response from streaming updates'
)

count = content.count(old)
print(f'Occurrences: {count}')
if count == 1:
    content2 = content.replace(old, new, 1)
    open('/copilot-home/SharpCoder/src/SharpCoder/CodingAgent.cs', 'w').write(content2)
    print('Done')
else:
    print('NOT REPLACED - showing context around line 286:')
    lines = content.split('\n')
    for i in range(283, 302):
        print(f'{i+1}: {repr(lines[i])}')
