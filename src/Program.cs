using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using OpenAI.Responses;
using System.Text.Json;
using System;
using System.Diagnostics;
using System.Reflection.PortableExecutable;



if (args.Length < 2 || args[0] != "-p")
{
    throw new Exception("Usage: program -p <prompt>");
}

var prompt = args[1];

Agent agent = new Agent(prompt);
agent.RunAgent();



class Agent
{
    private string apiKey;
    private string baseUrl;
    private string prompt;
    private ChatClient? client;
    List<ChatMessage> messages;

    // Tool Options
    private ChatCompletionOptions options;

    // Initialize the Agent
    public  Agent(string Prompt)
    {
        apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "YOUR_OPENROUTER_API_KEY";
        baseUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ?? "https://openrouter.ai/api/v1";

        if (string.IsNullOrEmpty(Prompt))
        {
            throw new Exception("Prompt must not be empty");
        }

        this.prompt = Prompt;


        client = new ChatClient(
          model: "anthropic/claude-haiku-4.5",
          //  model: "openrouter/free",
          credential: new ApiKeyCredential(apiKey),
          options: new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
        );

        messages = [new UserChatMessage(prompt)];
        InitOptions();
    }

    // Execute the Agent Loop
    // The Agent Loop runs until the Current Chat Loop is finished and if no action is required, i.e a new Message etc, will exit the Loop
    public void RunAgent()
    {
        bool requiresAction;
        do
        {
            requiresAction = false;
            ChatCompletion completion = client.CompleteChat(messages, options);
            // Handle the Chat Completions cases
            switch (completion.FinishReason)
            {   
                // Chat Has Ended
                case ChatFinishReason.Stop:
                    messages.Add(new AssistantChatMessage(completion));
                    Console.WriteLine($"{completion.Content[0].Text}");
                    break;
                // Tool Call was used
                case ChatFinishReason.ToolCalls:

                    // Create and Copy the Chat to the Assistent
                    messages.Add(new AssistantChatMessage(completion));

                    foreach (ChatToolCall toolCall in completion.ToolCalls)
                    {
                       
                        // Selecting the fitting Tool Call
                        switch (toolCall.FunctionName)
                        {
                            case "read_tool":
                                {
                                    // The arguments that the model wants to use to call the function are specified as a
                                    // stringified JSON object based on the schema defined in the tool definition. Note that
                                    // the model may hallucinate arguments too. Consequently, it is important to do the
                                    // appropriate parsing and validation before calling the function.
                                    using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);

                                    // Aliases from the Model Hallucinations, may not cover all
                                    var aliases = new[] { "file_path", "filepath", "path", "file", "filename", "parameter" };

                                    // To check for a supplied file Path and saving it as path if exist
                                    bool hasFilepath = TryGetStringProperty(argumentsJson.RootElement, aliases, out var path);
                                    //bool hasFilepath = argumentsJson.RootElement.TryGetProperty("file_path", out JsonElement path);

                                    // If No Filepath Exist
                                    if (!hasFilepath)
                                    {   
                                        // Function to dump the Model Parameter Hallucination...
                                        DumpHallucination(argumentsJson.RootElement.GetRawText(), "read_tool");
                                        throw new ArgumentNullException(nameof(toolCall.FunctionArguments), $"The Path was not found or the Path Hallucinated, Path {path}");
                                    }

                                    // Wrong Error Handling and setting the Tool Result
                                    string toolResult = hasFilepath ? ReadFile(path.ToString()) : argumentsJson.RootElement.GetProperty("path").ToString();
                                    
                                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                                    break;
                                }
                            case "write_tool":
                                {   
                                    // Same as read_tool except calling the WriteFile Function

                                    using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
                                    var aliases = new[] { "file_path", "filepath", "path", "file", "filename", "parameter" };
                                    bool hasFilepath = TryGetStringProperty(argumentsJson.RootElement, aliases, out var path);

                                    //Console.WriteLine($"Debug: Path:= {path}");
                                    if (!hasFilepath)
                                    {
                                        DumpHallucination(argumentsJson.RootElement.GetRawText(), "write_tool");
                                        throw new ArgumentNullException(nameof(toolCall.FunctionArguments), $"The Path was not found or the Path Hallucinated, Path {path}");
                                    }
                                    aliases = new[] { "content" };
                                    bool hasContent = TryGetStringProperty(argumentsJson.RootElement, aliases, out var content);
                                    if (!hasContent)
                                    {
                                        DumpHallucination(argumentsJson.RootElement.GetRawText(), "write_tool");
                                        throw new ArgumentNullException(nameof(toolCall.FunctionArguments), $"The content could be not be extracted to write {content}");
                                    }
                                    // Kann bestimmt verbessert werden
                                    string toolResult = WriteFile(path, content);
                                    //Console.WriteLine($"Tool Result: {toolResult}");
                                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                                    break;
                                }
                            case "bash_tool":
                                {
                                    // Same as read_tool except calling the ExecuteShellCommand Function

                                    using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
                                    var aliases = new[] { "command", "parameter", "FunctionArguments" };
                                    bool hasCommand = TryGetStringProperty(argumentsJson.RootElement, aliases, out var command);

                                    
                                    if (!hasCommand)
                                    {
                                        DumpHallucination(argumentsJson.RootElement.GetRawText(), "bash_tool");
                                        throw new ArgumentNullException(nameof(toolCall.FunctionArguments), $"The Command does not exist or was Hallucinated, Command {command}");
                                    }
                                    // Kann bestimmt verbessert werden
                                    string toolResult = ExecuteShellCommand("bash", command);
                                    
                                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                                    break;
                                }
                            case "powershell_tool":
                                {
                                    using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
                                    var aliases = new[] { "command", "parameter", "FunctionArguments" };
                                    bool hasCommand = TryGetStringProperty(argumentsJson.RootElement, aliases, out var command);

                                    //Console.WriteLine($"Debug: Path:= {path}");
                                    if (!hasCommand)
                                    {
                                        DumpHallucination(argumentsJson.RootElement.GetRawText(), "powershell_tool");
                                        throw new ArgumentNullException(nameof(toolCall.FunctionArguments), $"The Command does not exist or was Hallucinated, Command {command}");
                                    }
                                    // Kann bestimmt verbessert werden
                                    string toolResult = ExecuteShellCommand("powershell.exe", command);
                                    
                                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                                    break;
                                }
                            default:
                                {
                                    throw new NotImplementedException($"Unknown function {completion.FinishReason}");

                                }
                        }
                    }
                    requiresAction = true;
                    break;
                default:
                    throw new NotImplementedException(completion.FinishReason.ToString());
            }
        } while (requiresAction);
    }

    // Initialize the Options / Advertise the Agent Tools
    void InitOptions()
    {
        // Read Tool Spezifikation
        var readTool = ChatTool.CreateFunctionTool(
          functionName: "read_tool",
          functionDescription: "Read the content of a file",
          functionParameters: BinaryData.FromBytes("""

                                               {
                                                 "type": "function",
                                                 "function": {
                                                   "name": "Read",
                                                   "description": "Read and return the contents of a file",
                                                   "parameters": {
                                                     "type": "object",
                                                     "properties": {
                                                       "file_path": {
                                                         "type": "string",
                                                         "description": "The path to the file to read"
                                                       }
                                                     },
                                                     "required": ["file_path"]
                                                   }
                                                 }
                                               }
                                               """u8.ToArray())
        );


        // Write Tool Spezifikation
        var writeTool = ChatTool.CreateFunctionTool(
          functionName: "write_tool",
          functionDescription: "Write the content to a file",
          functionParameters: BinaryData.FromBytes("""
                                               {
                                                 "type": "function",
                                                 "function": {
                                                   "name": "Write",
                                                   "description": "Write content to a file",
                                                   "parameters": {
                                                     "type": "object",
                                                     "required": ["file_path", "content"],
                                                     "properties": {
                                                       "file_path": {
                                                         "type": "string",
                                                         "description": "The path of the file to write to"
                                                       },
                                                       "content": {
                                                         "type": "string",
                                                         "description": "The content to write to the file"
                                                       }
                                                     }
                                                   }
                                                 }
                                               }
                                               """u8.ToArray())
        );

        // Bash Command Execution Tool Specification

        var bashTool = ChatTool.CreateFunctionTool(
          functionName: "bash_tool",
          functionDescription: "Execute a Bash Shell Commands",
          functionParameters: BinaryData.FromBytes("""
                                               {
                                                 "type": "function",
                                                 "function": {
                                                   "name": "Bash",
                                                   "description": "Execute a shell command",
                                                   "parameters": {
                                                     "type": "object",
                                                     "required": ["command"],
                                                     "properties": {
                                                       "command": {
                                                         "type": "string",
                                                         "description": "The command to execute"
                                                       }
                                                     }
                                                   }
                                                 }
                                               }
                                               """u8.ToArray())
        );

        var powershellTool = ChatTool.CreateFunctionTool(
          functionName: "powershell_tool",
          functionDescription: "Execute a Powershell Shell Commands",
          functionParameters: BinaryData.FromBytes("""
                                               {
                                                 "type": "function",
                                                 "function": {
                                                   "name": "Powershell",
                                                   "description": "Execute a shell command",
                                                   "parameters": {
                                                     "type": "object",
                                                     "required": ["command"],
                                                     "properties": {
                                                       "command": {
                                                         "type": "string",
                                                         "description": "The command to execute"
                                                       }
                                                     }
                                                   }
                                                 }
                                               }
                                               """u8.ToArray())
        );


        // Die Option für den Chat erstellen / Tools Advertisment
        options = new() { Tools = { readTool, writeTool, bashTool, powershellTool } };

    }
    // Helper Function to Read a File
    static string ReadFile(string path)
    {
        if (File.Exists(path))
            return File.ReadAllText(path);
        else
        {
            throw new($"File not found {path}");
        }
    }

    // Helper Functio to Write to a File.
    // Currently only creates and or overwrites it
    static string WriteFile(string path, string content)
    {
        if (File.Exists(path))
        {
            File.WriteAllText(path, content);
            return "Successfully written file!";
        }
        else
        {
            File.WriteAllText(path, content);
            return "Successfully written file!";
        }

    }


    // Execute a Shell Command based on the supplied Shell (filename).
    static string ExecuteShellCommand(string fileName, string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = ".",
            ArgumentList = { "-c", command }
        };
        using var executor = Process.Start(startInfo)!;
        string output = executor.StandardOutput.ReadToEnd();
        string error = executor.StandardError.ReadToEnd();
        executor.WaitForExit();

        return string.IsNullOrEmpty(error) ? output : error;

    }
    // Hellper Function whichs writes the Model Tool Call Hallucinations to a Log file
    static void DumpHallucination(string content, string toolName)
    {
        if (File.Exists($"Hallucination_{toolName}.log"))
        {
            File.AppendAllText($"Hallucination_{toolName}.log", content);
        }
        else
        {
            File.WriteAllLines($"Hallucination_{toolName}.log", new[] { $"{content}" });
        }
    }


    // Helper Function to work with Dump Hallucination in Harmony
    static bool TryGetStringProperty(JsonElement root, string[] aliases, out string value)
    {
        foreach (var name in aliases)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                value = el.GetString()!;
                return true;
            }
        }

        value = null;
        return false;

    }


}