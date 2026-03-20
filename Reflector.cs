using System;
using System.Linq;
using Microsoft.Extensions.AI;
class Program { 
    static void Main() { 
        foreach(var c in typeof(OllamaChatClient).GetConstructors()) {
            Console.WriteLine("OllamaChatClient: " + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)));
        }
    } 
}
