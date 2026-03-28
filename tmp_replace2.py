content = open('/copilot-home/SharpCoder/src/SharpCoder/CodingAgent.cs').read()

old = (
    '                messages.Add(resultMessage);\n'
    '                allResponseMessages.Add(resultMessage);\n'
    '            }\n'
    '        }\n'
    '\n'
    '        // Update session with all messages'
)

new = (
    '                messages.Add(resultMessage);\n'
    '                allResponseMessages.Add(resultMessage);\n'
    '            }\n'
    '\n'
    '            // Mid-loop compaction: check before next API call\n'
    '            await _compactor.CompactIfNeededAsync(session, messages, _options, ct);\n'
    '        }\n'
    '\n'
    '        // Update session with all messages'
)

count = content.count(old)
print(f'Occurrences: {count}')
if count == 1:
    content2 = content.replace(old, new, 1)
    open('/copilot-home/SharpCoder/src/SharpCoder/CodingAgent.cs', 'w').write(content2)
    print('Done')
else:
    print('NOT REPLACED')
    # Show context around the area
    lines = content.split('\n')
    for i in range(355, 380):
        print(f'{i+1}: {repr(lines[i])}')
