using UnityEngine;
using UnityEditor;
using UnityEditor.VersionControl;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/// <summary>
/// Perforce 自动同步监控 - 使用 p4 命令行版本
/// 直接调用 p4 命令检查服务器变更（最可靠的方法）
/// 使用方法：Window -> Perforce Auto Sync Monitor
/// </summary>
public class PerforceAutoSyncMonitorEN : EditorWindow
{
    private int checkInterval = 1800;
    private string monitorPath = "Assets"; // Unity project path, such as"Assets"or"Assets/Scripts"
    private string logFilePath = "changes_log.txt";
    private bool isMonitoring;
    private string statusMessage = "Not started";
    private string lastError = string.Empty;
    private System.DateTime lastCheckTime;
    private int changesDetected;
    private bool autoSync = true;  // 检测到变更后是否自动同步
    private bool autoExport = true;  // 同步后是否自动上传到 ShareCreators
    
    // 文件扩展名白名单（只上传这些类型的文件）
    private string uploadExtensionsWhitelist = ".prefab,.fbx,.tga,.png";  // 默认白名单
    
    // Perforce 配置（从 Unity Settings 或环境变量读取）
    private string p4Port = string.Empty;
    private string p4User = string.Empty;
    private string p4Client = string.Empty;
    private string p4ExePath = "p4";  // p4.exe 的完整路径或命令名
    private string p4Password = string.Empty;  // Perforce 密码（可选，用于自动登录）

    [MenuItem("Window/Perforce Auto Sync Monitor (EN)")]
    public static void ShowWindow()
    {
        GetWindow<PerforceAutoSyncMonitorEN>("P4 Auto Sync EN");
    }

    private void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;
        // 启动时加载保存的配置
        LoadP4Settings();
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        if (!isMonitoring) return;

        var timeSinceLastCheck = (System.DateTime.Now - lastCheckTime).TotalSeconds;
        if (timeSinceLastCheck >= checkInterval)
        {
            CheckForRemoteChanges();
            lastCheckTime = System.DateTime.Now;
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("Perforce Auto Sync Monitor", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // Perforce 配置区域
        EditorGUILayout.LabelField("Perforce configuration", EditorStyles.boldLabel);
        EditorGUI.BeginDisabledGroup(isMonitoring);
        
        p4ExePath = EditorGUILayout.TextField("P4 execution file path", p4ExePath);
        EditorGUILayout.HelpBox("Usually 'p4' or 'p4.exe', if not in PATH please fill in the full path", MessageType.Info);
        
        p4Port = EditorGUILayout.TextField("P4PORT (server)", p4Port);
        p4User = EditorGUILayout.TextField("P4USER (username)", p4User);
        p4Password = EditorGUILayout.PasswordField("P4PASSWD (password)", p4Password);
        p4Client = EditorGUILayout.TextField("P4CLIENT (Workspace)", p4Client);
        EditorGUILayout.HelpBox("The password is used to log in automatically when the session expires. Leave blank to manually execute 'p4 login'", MessageType.Info);
        
        EditorGUILayout.BeginHorizontal();
        // if (GUILayout.Button("Find p4.exe"))
        // {
        //     FindP4Executable();
        //     Repaint();
        // }
        if (GUILayout.Button("Automatically detect configuration"))
        {
            LoadP4Settings();
            Repaint();
        }
        if (GUILayout.Button("Save configuration"))
        {
            SaveP4Settings(true);
        }
        if (GUILayout.Button("Test Connection"))
        {
            TestP4Connection();
        }
        if (GUILayout.Button("Login P4"))
        {
            LoginToP4();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUI.EndDisabledGroup();
        
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Monitoring settings", EditorStyles.boldLabel);
        
        EditorGUI.BeginDisabledGroup(isMonitoring);
        checkInterval = EditorGUILayout.IntSlider("Check interval (seconds)", checkInterval, 20, 3600);
        monitorPath = EditorGUILayout.TextField("Monitoring path", monitorPath);
        EditorGUILayout.HelpBox("Supports configuring multiple paths, separated by semicolons (;). For example: Assets/Scripts;Assets/Prefabs", MessageType.Info);
        logFilePath = EditorGUILayout.TextField("Log File", logFilePath);
        autoSync = EditorGUILayout.Toggle("Automatically sync files", autoSync);
        EditorGUILayout.HelpBox(
            autoSync ? "✓ Automatically execute p4 sync to update files after detecting changes" : "❌ Only record changes, do not automatically synchronize files",
            autoSync ? MessageType.Info : MessageType.Warning);
        
        EditorGUI.BeginDisabledGroup(!autoSync);
        autoExport = EditorGUILayout.Toggle("Automatically upload to server", autoExport);
        EditorGUILayout.HelpBox(
            autoExport ? "✓ Automatically call the ShareCreators export plug-in to upload changed files after synchronization" : "❌ Only sync, not upload to server",
            autoExport ? MessageType.Info : MessageType.Warning);
        
        EditorGUI.BeginDisabledGroup(!autoExport);
        EditorGUILayout.LabelField("Upload file extension whitelist", EditorStyles.boldLabel);
        uploadExtensionsWhitelist = EditorGUILayout.TextField("Allowed extensions", uploadExtensionsWhitelist);
        EditorGUILayout.HelpBox(
            "Only files with these extensions will be uploaded to the SC server\n" +
            "Multiple extensions are separated by commas, such as: .prefab,.fbx,.tga,.png\n" +
            "Current whitelist:" + (string.IsNullOrEmpty(uploadExtensionsWhitelist) ? "All allowed" : uploadExtensionsWhitelist),
            MessageType.Info);
        EditorGUI.EndDisabledGroup();
        
        EditorGUI.EndDisabledGroup();
        
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(isMonitoring ? "Stop monitoring" : "Start monitoring"))
        {
            if (isMonitoring)
            {
                StopMonitoring();
            }
            else
            {
                SaveP4Settings(false);
                StartMonitoring();
            }
        }

        if (GUILayout.Button("Check manually once"))
        {
            CheckForRemoteChanges();
        }

        if (GUILayout.Button("Open change log"))
        {
            OpenLogFile();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(statusMessage, MessageType.Info);

        if (!string.IsNullOrEmpty(lastError))
        {
            EditorGUILayout.HelpBox(lastError, MessageType.Error);
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Instructions for use: \n" +
            "• Monitoring path: Unity project path, supports multiple paths separated by semicolons \n" +
            "Example: Assets or Assets/Scripts;Assets/Prefabs\n" +
            "• Log file: location where change records are saved (relative to the project root directory) \n" +
            "• Automatic synchronization: automatically execute p4 sync to update file \n after detecting changes" +
            "• Automatic upload: Automatically upload changed files to ShareCreators server \n after synchronization" +
            "• Password configuration: used for automatic login when session expires (optional) \n\n" +
            "Note: \n" +
            "• Perforce uses ticket authentication, which is valid for about 12 hours\n" +
            "• Login \n required for first time use or when ticket expires" +
            "• Automatically log in again after configuring a password",
            MessageType.None);
    }

    private void StartMonitoring()
    {
        // 检查 P4 配置是否完整
        if (string.IsNullOrEmpty(p4Port) || string.IsNullOrEmpty(p4User) || string.IsNullOrEmpty(p4Client))
        {
            var errorMsg = "Perforce configuration is incomplete! \n\n" +
                $"P4PORT: {(string.IsNullOrEmpty(p4Port) ? "❌ Not set" : "✓ " + p4Port)}\n" +
                $"P4USER: {(string.IsNullOrEmpty(p4User) ? "❌ Not set" : "✓ " + p4User)}\n" +
                $"P4CLIENT: {(string.IsNullOrEmpty(p4Client) ? "❌ Not set" : "✓ " + p4Client)}\n\n" +
                "Please fill in the Perforce configuration at the top of the window, \n" +
                "Or click the \"Automatically detect configuration\" button.";
            
            EditorUtility.DisplayDialog("Incomplete configuration", errorMsg, "OK");
            return;
        }
        
        // 测试 p4 命令是否可用
        if (!TestP4Command())
        {
            var errorMsg = "Unable to execute p4 command. \n\n" +
                "Current configuration: \n" +
                $"P4PORT: {p4Port}\n" +
                $"P4USER: {p4User}\n" +
                $"P4CLIENT: {p4Client}\n\n" +
                "Please make sure: \n" +
                "1. Perforce client (p4.exe)\n installed" +
                "2. p4.exe is in the system PATH \n" +
                "3. The configuration information is correct\n" +
                "4. Run 'p4 login' to log in to \n\n" +
                "It is recommended to click the \"Test Connection\" button to view detailed information.";
            
            EditorUtility.DisplayDialog("P4 command execution failed", errorMsg, "OK");
            return;
        }

        isMonitoring = true;
        lastCheckTime = System.DateTime.Now;
        changesDetected = 0;
        statusMessage = $"Monitoring started \n Interval: {checkInterval} seconds \n Path: {monitorPath}\n Log: {logFilePath}";
        lastError = string.Empty;

        UnityEngine.Debug.Log($"[P4 Auto Sync] Start monitoring: interval={checkInterval}s, path={monitorPath}");
        
        // 立即执行一次检查
        CheckForRemoteChanges();
    }

    private void StopMonitoring()
    {
        isMonitoring = false;
        statusMessage = "Monitoring has stopped";
        UnityEngine.Debug.Log("[P4 Auto Sync] Stop monitoring");
    }

    private void LoadP4Settings()
    {
        // 从 EditorPrefs 读取保存的配置
        p4ExePath = EditorPrefs.GetString("P4AutoSync_P4ExePath", "p4");
        p4Port = EditorPrefs.GetString("P4AutoSync_P4PORT", "");
        p4User = EditorPrefs.GetString("P4AutoSync_P4USER", "");
        p4Client = EditorPrefs.GetString("P4AutoSync_P4CLIENT", "");
        p4Password = EditorPrefs.GetString("P4AutoSync_P4PASSWD", "");
        uploadExtensionsWhitelist = EditorPrefs.GetString("P4AutoSync_UploadExtensions", ".prefab,.fbx,.tga,.png");
        
        // 如果 p4ExePath 是默认值，尝试查找
        if (p4ExePath == "p4" || string.IsNullOrEmpty(p4ExePath))
        {
            // FindP4Executable();
        }
        
        // 如果没有保存的配置，尝试从环境变量读取
        if (string.IsNullOrEmpty(p4Port))
            p4Port = System.Environment.GetEnvironmentVariable("P4PORT") ?? "";
        if (string.IsNullOrEmpty(p4User))
            p4User = System.Environment.GetEnvironmentVariable("P4USER") ?? "";
        if (string.IsNullOrEmpty(p4Client))
            p4Client = System.Environment.GetEnvironmentVariable("P4CLIENT") ?? "";
        if (string.IsNullOrEmpty(p4Password))
            p4Password = System.Environment.GetEnvironmentVariable("P4PASSWD") ?? "";
        
        // 如果还是为空，尝试从 p4 set 命令获取
        if (string.IsNullOrEmpty(p4Port) || string.IsNullOrEmpty(p4User) || string.IsNullOrEmpty(p4Client))
        {
            TryGetP4ConfigFromCommand();
        }
            
        UnityEngine.Debug.Log($"[P4 Auto Sync] P4 configuration loaded: EXE={p4ExePath}, PORT={p4Port}, USER={p4User}, CLIENT={p4Client}");
    }
    
    private void FindP4Executable()
    {
        UnityEngine.Debug.Log("[P4 Auto Sync] Find p4.exe...");
        
        // 常见的 Perforce 安装路径
        var commonPaths = new[]
        {
            @"C:\Program Files\Perforce\p4.exe",
            @"C:\Program Files (x86)\Perforce\p4.exe",
            @"C:\Perforce\p4.exe",
            System.Environment.GetEnvironmentVariable("ProgramFiles") + @"\Perforce\p4.exe",
            System.Environment.GetEnvironmentVariable("ProgramFiles(x86)") + @"\Perforce\p4.exe"
        };
        
        foreach (var path in commonPaths)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                p4ExePath = path;
                UnityEngine.Debug.Log($"[P4 Auto Sync] Found p4.exe: {p4ExePath}");
                EditorUtility.DisplayDialog("Success", $"Found p4.exe:\n{p4ExePath}", "OK");
                return;
            }
        }
        
        // 尝试使用 where 命令查找
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "p4",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(processInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var lines = output.Split('\n');
                    if (lines.Length > 0 && !string.IsNullOrEmpty(lines[0].Trim()))
                    {
                        p4ExePath = lines[0].Trim();
                        UnityEngine.Debug.Log($"[P4 Auto Sync] Find p4 via where command: {p4ExePath}");
                        EditorUtility.DisplayDialog("Success", $"Found p4.exe:\n{p4ExePath}", "OK");
                        return;
                    }
                }
            }
        }
        catch
        {
            // 忽略错误
        }
        
        EditorUtility.DisplayDialog("p4.exe not found", 
            "p4.exe not found. \n\n" +
            "Please manually enter the full path of p4.exe, \n" +
            "Or make sure p4.exe is in your system PATH. \n\n" +
            "Common location: \n" +
            "C:\\Program Files\\Perforce\\p4.exe", 
            "OK");
    }
    
    private void SaveP4Settings(bool showTips = false)
    {
        // 保存到 EditorPrefs
        EditorPrefs.SetString("P4AutoSync_P4ExePath", p4ExePath);
        EditorPrefs.SetString("P4AutoSync_P4PORT", p4Port);
        EditorPrefs.SetString("P4AutoSync_P4USER", p4User);
        EditorPrefs.SetString("P4AutoSync_P4CLIENT", p4Client);
        EditorPrefs.SetString("P4AutoSync_P4PASSWD", p4Password);
        EditorPrefs.SetString("P4AutoSync_UploadExtensions", uploadExtensionsWhitelist);
        
        UnityEngine.Debug.Log($"[P4 Auto Sync] P4 configuration saved: EXE={p4ExePath}, PORT={p4Port}, USER={p4User}, CLIENT={p4Client}");
        if (showTips)
        {
            EditorUtility.DisplayDialog("Success", "Perforce configuration saved!", "OK");
        }
    }
    
    private void TestP4Connection()
    {
        UnityEngine.Debug.Log("[P4 Auto Sync] Test P4 connection...");
        
        var processInfo = CreateP4ProcessInfo("info");
        
        try
        {
            using (var process = Process.Start(processInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                
                if (process.ExitCode == 0)
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] P4 connection successful!\n{output}");
                    
                    // 从输出中提取关键信息
                    var info = new StringBuilder();
                    info.AppendLine("Connection successful!");
                    info.AppendLine();
                    
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains("User name:") || 
                            line.Contains("Client name:") || 
                            line.Contains("Server address:") ||
                            line.Contains("Server version:"))
                        {
                            info.AppendLine(line.Trim());
                        }
                    }
                    
                    EditorUtility.DisplayDialog("P4 connection test", info.ToString(), "OK");
                }
                else
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] P4 connection failed! \n error: {error}");
                    EditorUtility.DisplayDialog("P4 connection test failed", 
                        $"Unable to connect to Perforce server. \n\nError message:\n{error}\n\nPlease check whether the configuration is correct.", 
                        "OK");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] P4 connection test failed: {ex.Message}");
            EditorUtility.DisplayDialog("P4 connection test failed", 
                $"Execution of p4 command failed. \n\nError:\n{ex.Message}\n\nPlease make sure p4.exe is installed and in the PATH.", 
                "OK");
        }
    }
    
    private void LoginToP4()
    {
        UnityEngine.Debug.Log("[P4 Auto Sync] Login to Perforce...");
        
        if (string.IsNullOrEmpty(p4Password))
        {
            EditorUtility.DisplayDialog("Password required", 
                "Please enter the Perforce password above, \n and then click \"Save Configuration\" and \"Login to P4\". \n\n" +
                "Or execute it manually on the command line: \np4 login", 
                "OK");
            return;
        }
        
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = p4ExePath,
                Arguments = "login",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            // 设置 P4 环境变量
            if (!string.IsNullOrEmpty(p4Port))
                processInfo.EnvironmentVariables["P4PORT"] = p4Port;
            if (!string.IsNullOrEmpty(p4User))
                processInfo.EnvironmentVariables["P4USER"] = p4User;
            if (!string.IsNullOrEmpty(p4Client))
                processInfo.EnvironmentVariables["P4CLIENT"] = p4Client;

            using (var process = Process.Start(processInfo))
            {
                // 向 p4 login 输入密码
                process.StandardInput.WriteLine(p4Password);
                process.StandardInput.Close();
                
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                
                if (process.ExitCode == 0 || output.Contains("logged in"))
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] Login successful!\n{output}");
                    EditorUtility.DisplayDialog("Login successful", 
                        "Successfully logged in to the Perforce server! \n\n" +
                        "A session ticket is generated and is typically valid for 12 hours.", 
                        "OK");
                }
                else
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] Login failed! \n error: {error}");
                    EditorUtility.DisplayDialog("Login failed", 
                        $"Unable to log in to Perforce server. \n\nError message:\n{error}\n\nPlease check whether the username and password are correct.", 
                        "OK");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] Login failed: {ex.Message}");
            EditorUtility.DisplayDialog("Login failed", 
                $"Execution of p4 login command failed. \n\n Error: \n{ex.Message}", 
                "OK");
        }
    }
    
    private bool TryAutoLogin()
    {
        if (string.IsNullOrEmpty(p4Password))
        {
            UnityEngine.Debug.LogWarning("[P4 Auto Sync] No password configured, unable to log in automatically");
            return false;
        }
        
        UnityEngine.Debug.Log("[P4 Auto Sync] Session expired, try to log in automatically...");
        
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = p4ExePath,
                Arguments = "login",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            if (!string.IsNullOrEmpty(p4Port))
                processInfo.EnvironmentVariables["P4PORT"] = p4Port;
            if (!string.IsNullOrEmpty(p4User))
                processInfo.EnvironmentVariables["P4USER"] = p4User;
            if (!string.IsNullOrEmpty(p4Client))
                processInfo.EnvironmentVariables["P4CLIENT"] = p4Client;

            using (var process = Process.Start(processInfo))
            {
                process.StandardInput.WriteLine(p4Password);
                process.StandardInput.Close();
                
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                
                if (process.ExitCode == 0 || output.Contains("logged in"))
                {
                    UnityEngine.Debug.Log("[P4 Auto Sync] Automatic login successful!");
                    return true;
                }
                else
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] Auto login failed: {error}");
                    return false;
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] Auto login exception: {ex.Message}");
            return false;
        }
    }
    
    private void TryGetP4ConfigFromCommand()
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = p4ExePath,
                Arguments = "set",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(processInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // 解析 p4 set 输出
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains("P4PORT=") && string.IsNullOrEmpty(p4Port))
                        {
                            var parts = line.Split('=');
                            if (parts.Length > 1)
                                p4Port = parts[1].Trim().Replace("(set)", "").Trim();
                        }
                        else if (line.Contains("P4USER=") && string.IsNullOrEmpty(p4User))
                        {
                            var parts = line.Split('=');
                            if (parts.Length > 1)
                                p4User = parts[1].Trim().Replace("(set)", "").Trim();
                        }
                        else if (line.Contains("P4CLIENT=") && string.IsNullOrEmpty(p4Client))
                        {
                            var parts = line.Split('=');
                            if (parts.Length > 1)
                                p4Client = parts[1].Trim().Replace("(set)", "").Trim();
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略错误，继续使用已有配置
        }
    }

    private bool TestP4Command()
    {
        LoadP4Settings();
        
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = p4ExePath,
                Arguments = "info",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Application.dataPath
            };
            
            // 显式设置环境变量
            if (!string.IsNullOrEmpty(p4Port))
                processInfo.EnvironmentVariables["P4PORT"] = p4Port;
            if (!string.IsNullOrEmpty(p4User))
                processInfo.EnvironmentVariables["P4USER"] = p4User;
            if (!string.IsNullOrEmpty(p4Client))
                processInfo.EnvironmentVariables["P4CLIENT"] = p4Client;

            using (var process = Process.Start(processInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                
                UnityEngine.Debug.Log($"[P4 Auto Sync] p4 info output:\n{output}");
                if (!string.IsNullOrEmpty(error))
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] p4 info error:\n{error}");
                }
                
                return process.ExitCode == 0;
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] p4 command test failed: {ex.Message}");
            return false;
        }
    }

    private ProcessStartInfo CreateP4ProcessInfo(string arguments)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = p4ExePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Application.dataPath
        };
        
        // 显式设置 P4 环境变量
        if (!string.IsNullOrEmpty(p4Port))
            processInfo.EnvironmentVariables["P4PORT"] = p4Port;
        if (!string.IsNullOrEmpty(p4User))
            processInfo.EnvironmentVariables["P4USER"] = p4User;
        if (!string.IsNullOrEmpty(p4Client))
            processInfo.EnvironmentVariables["P4CLIENT"] = p4Client;
            
        return processInfo;
    }

    private void CheckForRemoteChanges()
    {
        UnityEngine.Debug.Log($"[P4 Auto Sync] Check for remote changes: {monitorPath}");

        try
        {
            // 支持多个路径，用分号分隔
            var paths = monitorPath.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            var allChangedFiles = new List<string>();
            var allSyncedFiles = new List<string>();
            
            foreach (var path in paths)
            {
                var trimmedPath = path.Trim();
                if (string.IsNullOrEmpty(trimmedPath))
                    continue;
                    
                UnityEngine.Debug.Log($"[P4 Auto Sync] Check path: {trimmedPath}");
                
                // 转换 Unity 路径为文件系统路径
                string fullPath;
                if (trimmedPath == "Assets")
                {
                    fullPath = Application.dataPath;
                }
                else if (trimmedPath.StartsWith("Assets/"))
                {
                    fullPath = Path.Combine(Application.dataPath, trimmedPath.Substring(7));
                }
                else
                {
                    fullPath = trimmedPath;
                }

                // 获取 depot 路径
                string depotPath = GetDepotPathFromLocalPath(fullPath);
                if (string.IsNullOrEmpty(depotPath))
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] Unable to obtain depot path: {fullPath}");
                    continue;
                }

                // 检查服务器变更
                var changedFiles = CheckServerChangesWithP4(depotPath);
                
                if (changedFiles.Count > 0)
                {
                    allChangedFiles.AddRange(changedFiles);
                    
                    // 如果启用自动同步，执行 p4 sync
                    if (autoSync)
                    {
                        var syncedFiles = SyncChangedFiles(depotPath);
                        allSyncedFiles.AddRange(syncedFiles);
                    }
                }
            }
            
            // 记录所有变更
            if (allChangedFiles.Count > 0)
            {
                LogChanges(allChangedFiles);
                
                // 如果启用自动上传，调用 ShareCreators 导出插件
                if (autoSync && autoExport && allSyncedFiles.Count > 0)
                {
                    ExportChangedFilesToServer(allSyncedFiles);
                }
                
                changesDetected += allChangedFiles.Count;
                
                statusMessage = $"Found {allChangedFiles.Count} remote changes\n" +
                               (autoSync ? "Automatically synchronized file\n" : "") +
                               (autoExport && autoSync ? "Uploaded to server\n" : "") +
                               $"Total: {changesDetected} \n" +
                               $"Last check: {System.DateTime.Now:HH:mm:ss}";
                
                UnityEngine.Debug.Log($"[P4 Auto Sync] Found {allChangedFiles.Count} remote changes" + 
                    (autoSync ? ", synchronized" : "") + 
                    (autoExport && autoSync ? ", uploaded" : ""));
            }
            else
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] No remote changes found");
                statusMessage = $"No remote changes \n Last check: {System.DateTime.Now:HH:mm:ss}";
            }

            lastError = string.Empty;
        }
        catch (System.Exception ex)
        {
            lastError = ex.Message;
            UnityEngine.Debug.LogError($"[P4 Auto Sync] Check failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private string GetDepotPathFromLocalPath(string localPath)
    {
        try
        {
            var processInfo = CreateP4ProcessInfo($"where \"{localPath}\"");

            using (var process = Process.Start(processInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(error))
                {
                    // 检查是否是 session 过期错误
                    if (error.Contains("session has expired") || error.Contains("login again"))
                    {
                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Session has expired, trying to log in automatically...");
                        
                        if (TryAutoLogin())
                        {
                            // 登录成功，重试命令
                            UnityEngine.Debug.Log("[P4 Auto Sync] Automatic login successful, try again to obtain depot path...");
                            using (var retryProcess = Process.Start(processInfo))
                            {
                                output = retryProcess.StandardOutput.ReadToEnd();
                                error = retryProcess.StandardError.ReadToEnd();
                                retryProcess.WaitForExit();
                            }
                        }
                        else
                        {
                            // 自动登录失败，提示用户手动登录
                            lastError = "Perforce session has expired, please click the \"Login P4\" button to log in again";
                            EditorUtility.DisplayDialog("Session expired", 
                                "Perforce session has expired. \n\n" +
                                (string.IsNullOrEmpty(p4Password) 
                                    ? "Please click the \"Login P4\" button to log in again, \n or execute on the command line: p4 login"
                                    : "Automatic login failed, please check whether the password is correct, \n and then click the \"Login P4\" button."), 
                                "OK");
                            StopMonitoring();
                            return null;
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(error))
                    {
                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] p4 where warning: {error}");
                        return null;
                    }
                }

                if (!string.IsNullOrEmpty(output))
                {
                    // p4 where 输出格式: depot-path client-path local-path
                    var parts = output.Trim().Split(' ');
                    if (parts.Length > 0)
                    {
                        var depotPath = parts[0];
                        // 移除 #revision 后缀
                        var hashIndex = depotPath.IndexOf('#');
                        if (hashIndex > 0)
                        {
                            depotPath = depotPath.Substring(0, hashIndex);
                        }
                        return depotPath;
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] Failed to obtain depot path: {ex.Message}");
        }

        return null;
    }

    private List<string> CheckServerChangesWithP4(string depotPath)
    {
        var changedFiles = new List<string>();
        
        try
        {
            // 使用 p4 sync -n 预览需要同步的文件
            var processInfo = CreateP4ProcessInfo($"sync -n \"{depotPath}/...\"");

            using (var process = Process.Start(processInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(error) && !error.Contains("file(s) up-to-date"))
                {
                    UnityEngine.Debug.LogWarning($"[P4 Auto Sync] p4 sync -n warning: {error}");
                }

                if (!string.IsNullOrEmpty(output))
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] p4 sync -n output:\n{output}");
                    
                    // 解析输出
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        var trimmedLine = line.Trim();
                        if (string.IsNullOrEmpty(trimmedLine)) continue;
                        
                        // p4 sync -n 的输出格式：
                        // //depot/path/file.txt#3 - added as C:\workspace\path\file.txt (新增)
                        // //depot/path/file.txt#5 - updating C:\workspace\path\file.txt (修改)
                        // //depot/path/file.txt#7 - deleted as C:\workspace\path\file.txt (删除)
                        if (trimmedLine.StartsWith("//"))
                        {
                            string changeType = null;
                            if (trimmedLine.Contains(" - added as "))
                                changeType = "New";
                            else if (trimmedLine.Contains(" - updating "))
                                changeType = "Revise";
                            else if (trimmedLine.Contains(" - deleted as "))
                                changeType = "delete";
                            
                            if (changeType != null)
                            {
                                var parts = trimmedLine.Split(new[] { " - " }, System.StringSplitOptions.None);
                                if (parts.Length >= 2)
                                {
                                    var depotFile = parts[0];
                                    // 移除 #revision
                                    var hashIdx = depotFile.IndexOf('#');
                                    if (hashIdx > 0)
                                    {
                                        depotFile = depotFile.Substring(0, hashIdx);
                                    }
                                    
                                    changedFiles.Add($"[{changeType}] {depotFile}");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] Failed to check for server changes: {ex.Message}");
        }

        return changedFiles;
    }

    private List<string> SyncChangedFiles(string depotPath)
    {
        var syncedFiles = new List<string>();
        
        try
        {
            UnityEngine.Debug.Log($"[P4 Auto Sync] Start synchronizing files: {depotPath}/...");
            
            // 先使用 p4 sync -n 获取需要同步的文件列表
            var previewInfo = CreateP4ProcessInfo($"sync -n \"{depotPath}/...\"");
            var filesToSync = new List<string>();      // 新增和修改的文件
            var filesToDelete = new List<string>();    // 需要删除的文件
            
            using (var previewProcess = Process.Start(previewInfo))
            {
                var previewOutput = previewProcess.StandardOutput.ReadToEnd();
                previewProcess.WaitForExit();
                
                if (!string.IsNullOrEmpty(previewOutput))
                {
                    var lines = previewOutput.Split('\n');
                    foreach (var line in lines)
                    {
                        var trimmedLine = line.Trim();
                        if (string.IsNullOrEmpty(trimmedLine) || !trimmedLine.StartsWith("//")) continue;
                        
                        // p4 sync -n 输出格式：
                        // //depot/path/file.txt#3 - added as C:\workspace\path\file.txt
                        // //depot/path/file with spaces.txt#5 - updating C:\workspace\path\file with spaces.txt
                        // //depot/path/file#name.txt#7 - deleted as C:\workspace\path\file#name.txt
                        
                        // 使用完整的操作类型标记进行匹配，避免文件名中的 " - " 干扰解析
                        // p4 sync -n 输出格式: //depot/path/文件 - 副本.jpg#3 - added as C:\...
                        string depotFile = null;
                        
                        // 区分删除操作和其他操作
                        if (trimmedLine.Contains(" - deleted as ") || trimmedLine.Contains(" - deleting "))
                        {
                            // 从后往前查找操作类型标记，避免文件名中的 " - " 干扰
                            var idx = trimmedLine.LastIndexOf(" - deleted as ");
                            if (idx < 0) idx = trimmedLine.LastIndexOf(" - deleting ");
                            
                            if (idx > 0)
                            {
                                // 提取操作标记之前的部分（包含 depot 路径和版本号）
                                var depotPart = trimmedLine.Substring(0, idx);
                                
                                // 从后往前查找 # 号（版本号标记）
                                var hashIdx = depotPart.LastIndexOf('#');
                                if (hashIdx > 0)
                                {
                                    depotFile = depotPart.Substring(0, hashIdx);
                                    filesToDelete.Add(depotFile);
                                    UnityEngine.Debug.Log($"[P4 Auto Sync] Delete operation detected: {depotFile}");
                                }
                            }
                        }
                        else if (trimmedLine.Contains(" - added as ") || 
                                 trimmedLine.Contains(" - updating ") || 
                                 trimmedLine.Contains(" - refreshing "))
                        {
                            // 从后往前查找操作类型标记
                            var idx = trimmedLine.LastIndexOf(" - added as ");
                            if (idx < 0) idx = trimmedLine.LastIndexOf(" - updating ");
                            if (idx < 0) idx = trimmedLine.LastIndexOf(" - refreshing ");
                            
                            if (idx > 0)
                            {
                                // 提取操作标记之前的部分（包含 depot 路径和版本号）
                                var depotPart = trimmedLine.Substring(0, idx);
                                
                                // 从后往前查找 # 号（版本号标记）
                                var hashIdx = depotPart.LastIndexOf('#');
                                if (hashIdx > 0)
                                {
                                    depotFile = depotPart.Substring(0, hashIdx);
                                    filesToSync.Add(depotFile);
                                }
                            }
                        }
                    }
                }
            }
            
            if (filesToSync.Count == 0 && filesToDelete.Count == 0)
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] No files need to be synchronized");
                return syncedFiles;
            }
            
            UnityEngine.Debug.Log($"[P4 Auto Sync] Prepare to synchronize {filesToSync.Count} files, delete {filesToDelete.Count} files");
            
            // 1. 先同步删除操作（使用 -f 强制同步，允许删除可写文件）
            if (filesToDelete.Count > 0)
            {
                var deleteInfo = CreateP4ProcessInfo($"sync -f {string.Join(" ", filesToDelete.Select(f => $"\"{f}\""))}");
                using (var deleteProcess = Process.Start(deleteInfo))
                {
                    var deleteOutput = deleteProcess.StandardOutput.ReadToEnd();
                    var deleteError = deleteProcess.StandardError.ReadToEnd();
                    deleteProcess.WaitForExit();
                    
                    if (!string.IsNullOrEmpty(deleteOutput))
                    {
                        UnityEngine.Debug.Log($"[P4 Auto Sync] Delete operation completed:\n{deleteOutput}");
                    }
                    if (!string.IsNullOrEmpty(deleteError) && !deleteError.Contains("file(s) up-to-date"))
                    {
                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Delete operation warning: {deleteError}");
                    }
                }
            }
            
            // 2. 对新增/修改的文件执行 p4 sync -f（强制同步，解决 "Can't clobber writable file" 错误）
            if (filesToSync.Count == 0)
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] No files need to be added or updated");
                return syncedFiles;
            }
            
            var processInfo = CreateP4ProcessInfo($"sync -f {string.Join(" ", filesToSync.Select(f => $"\"{f}\""))}");

            using (var process = Process.Start(processInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(output))
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] Synchronization successful:\n{output}");
                    
                    // 解析同步的文件列表
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        var trimmedLine = line.Trim();
                        if (string.IsNullOrEmpty(trimmedLine)) continue;
                        
                        // p4 sync 输出格式：
                        // //depot/path/file.txt#3 - added as C:\workspace\path\file.txt (新增)
                        // //depot/path/file.txt#5 - updating C:\workspace\path\file.txt (修改)
                        // //depot/path/file.txt#7 - deleted as C:\workspace\path\file.txt (删除 - 需要跳过)
                        // //depot/path/file.txt#8 - is opened and not being changed (文件已打开，未同步)
                        if (trimmedLine.StartsWith("//"))
                        {
                            // 跳过删除操作 - 删除的文件不存在，无法上传
                            if (trimmedLine.Contains(" - deleted as ") || trimmedLine.Contains(" - deleting "))
                            {
                                UnityEngine.Debug.Log($"[P4 Auto Sync] Skip deleted files: {trimmedLine}");
                                continue;
                            }
                            
                            // 跳过已打开但未改变的文件
                            if (trimmedLine.Contains(" - is opened and not being changed"))
                            {
                                UnityEngine.Debug.Log($"[P4 Auto Sync] Skip open but unchanged files: {trimmedLine}");
                                continue;
                            }
                            
                            string localPath = null;
                            
                            // 尝试匹配 "- added as" 格式
                            if (trimmedLine.Contains(" - added as "))
                            {
                                var parts = trimmedLine.Split(new[] { " - added as " }, System.StringSplitOptions.None);
                                if (parts.Length >= 2)
                                {
                                    localPath = parts[1].Trim();
                                    UnityEngine.Debug.Log($"[P4 Auto Sync] Parse 'added as' format: {localPath}");
                                }
                            }
                            // 尝试匹配 "- updating" 格式
                            else if (trimmedLine.Contains(" - updating "))
                            {
                                var parts = trimmedLine.Split(new[] { " - updating " }, System.StringSplitOptions.None);
                                if (parts.Length >= 2)
                                {
                                    localPath = parts[1].Trim();
                                    UnityEngine.Debug.Log($"[P4 Auto Sync] Parse 'updating' format: {localPath}");
                                }
                            }
                            // 尝试匹配 "- refreshing" 格式
                            else if (trimmedLine.Contains(" - refreshing "))
                            {
                                var parts = trimmedLine.Split(new[] { " - refreshing " }, System.StringSplitOptions.None);
                                if (parts.Length >= 2)
                                {
                                    localPath = parts[1].Trim();
                                    UnityEngine.Debug.Log($"[P4 Auto Sync] Parse 'refreshing' format: {localPath}");
                                }
                            }
                            
                            // 如果成功提取了本地路径
                            if (!string.IsNullOrEmpty(localPath))
                            {
                                // 检查是否是有效的文件路径（包含盘符）
                                if (localPath.Contains(":"))
                                {
                                    // 转换为 Unity 资源路径
                                    var assetPath = ConvertToAssetPath(localPath);
                                    if (!string.IsNullOrEmpty(assetPath))
                                    {
                                        // 验证文件确实存在才添加到列表（防止删除操作被误加入）
                                        var fullPath = Path.Combine(Application.dataPath, assetPath.Substring(7));
                                        if (File.Exists(fullPath))
                                        {
                                            syncedFiles.Add(assetPath);
                                            UnityEngine.Debug.Log($"[P4 Auto Sync] Added sync file to list: {assetPath}");
                                        }
                                        else
                                        {
                                            UnityEngine.Debug.LogWarning($"[P4 Auto Sync] File does not exist, skipped: {assetPath} (possibly deletion operation)");
                                        }
                                    }
                                    else
                                    {
                                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Conversion to Asset path failed: {localPath}");
                                    }
                                }
                                else
                                {
                                    UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Invalid path format (missing drive letter): {localPath}");
                                }
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(error) && !error.Contains("file(s) up-to-date"))
                {
                    UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Sync warning: {error}");
                }
                
                if (process.ExitCode == 0)
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] Files have been synced to the latest version, a total of {syncedFiles.Count} files can be uploaded");
                    // 刷新 Unity 资源数据库
                    AssetDatabase.Refresh();
                }
                else
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] Synchronization failed, exit code: {process.ExitCode}");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] Failed to synchronize files: {ex.Message}");
        }
        
        return syncedFiles;
    }
    
    private string ConvertToAssetPath(string localPath)
    {
        try
        {
            // 将本地文件路径转换为 Unity Assets 路径
            var dataPath = Application.dataPath;
            localPath = localPath.Replace("\\", "/");
            
            if (localPath.StartsWith(dataPath, System.StringComparison.OrdinalIgnoreCase))
            {
                // 转换为 Assets/... 格式
                var relativePath = localPath.Substring(dataPath.Length);
                if (relativePath.StartsWith("/"))
                {
                    relativePath = relativePath.Substring(1);
                }
                return "Assets/" + relativePath;
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Conversion path failed: {localPath}, Error: {ex.Message}");
        }
        
        return null;
    }
    
    private void ExportChangedFilesToServer(List<string> syncedFiles)
    {
#if UNITY_EDITOR
        try
        {
            if (syncedFiles.Count == 0)
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] No files to upload");
                return;
            }
            
            UnityEngine.Debug.Log($"[P4 Auto Sync] Starting upload of {syncedFiles.Count} files to ShareCreators server...");
            
            // 使用反射获取 ExporterScriptableObject 类型和实例
            System.Type exporterType = null;
            
            // 遍历所有程序集查找类型
            UnityEngine.Debug.Log("[P4 Auto Sync] Start looking for ExporterScriptableObject type...");
            var allAssemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            
            foreach (var assembly in allAssemblies)
            {
                var assemblyName = assembly.GetName().Name;
                
                // 重点查找 ShareCreatorEditor 程序集
                if (assemblyName == "ShareCreatorEditor" || 
                    assemblyName.Contains("ShareCreator") ||
                    assemblyName == "Assembly-CSharp-Editor" ||
                    assemblyName == "Assembly-CSharp")
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] Checking assembly: {assemblyName}");
                    
                    try
                    {
                        exporterType = assembly.GetType("ShareCreators.Exporter.ExporterScriptableObject");
                        if (exporterType != null)
                        {
                            UnityEngine.Debug.Log($"[P4 Auto Sync] ✓ Found ExporterScriptableObject type in assembly {assemblyName}");
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Error checking assembly {assemblyName}: {ex.Message}");
                    }
                }
            }
            
            if (exporterType == null)
            {
                // 列出所有可能相关的程序集和类型，帮助调试
                UnityEngine.Debug.LogError("[P4 Auto Sync] ❌ Unable to find ShareCreators.Exporter.ExporterScriptableObject type");
                UnityEngine.Debug.Log("[P4 Auto Sync] List of available assemblies (ShareCreator related):");
                
                var foundShareCreatorAssembly = false;
                foreach (var assembly in allAssemblies)
                {
                    var name = assembly.GetName().Name;
                    if (name.Contains("ShareCreator") || name.Contains("Editor") && name.Contains("CSharp"))
                    {
                        foundShareCreatorAssembly = true;
                        UnityEngine.Debug.Log($"  - {name}");
                        
                        // 尝试列出该程序集中的所有类型
                        try
                        {
                            var types = assembly.GetTypes();
                            var shareCreatorTypes = new List<string>();
                            foreach (var type in types)
                            {
                                if (type.FullName != null && type.FullName.Contains("ShareCreators"))
                                {
                                    shareCreatorTypes.Add(type.FullName);
                                }
                            }
                            
                            if (shareCreatorTypes.Count > 0)
                            {
                                UnityEngine.Debug.Log($"Contains {shareCreatorTypes.Count} ShareCreators types:");
                                foreach (var typeName in shareCreatorTypes)
                                {
                                    UnityEngine.Debug.Log($"      → {typeName}");
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            UnityEngine.Debug.LogWarning($"Unable to list types: {ex.Message}");
                        }
                    }
                }
                
                if (!foundShareCreatorAssembly)
                {
                    UnityEngine.Debug.LogError("[P4 Auto Sync] ⚠️ No ShareCreator related assemblies found!");
                    UnityEngine.Debug.Log("[P4 Auto Sync] Tip: Please make sure the ShareCreators plugin is properly installed in the Unity project");
                }
                
                return;
            }
            
            // 调用 GetInstance() 静态方法
            var getInstanceMethod = exporterType.GetMethod("GetInstance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (getInstanceMethod == null)
            {
                UnityEngine.Debug.LogError("[P4 Auto Sync] Unable to find GetInstance method");
                return;
            }
            
            var exporter = getInstanceMethod.Invoke(null, null);
            if (exporter == null)
            {
                UnityEngine.Debug.LogError("[P4 Auto Sync] Unable to get ExporterScriptableObject instance");
                return;
            }
            
            // 检查 IsExporting 属性
            var isExportingProperty = exporterType.GetProperty("IsExporting", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (isExportingProperty != null)
            {
                var isExporting = (bool)isExportingProperty.GetValue(exporter);
                if (isExporting)
                {
                    UnityEngine.Debug.LogWarning("[P4 Auto Sync] ShareCreators are exporting other files, skip this upload");
                    return;
                }
            }
            
            // 准备允许的扩展名列表
            var allowedExtensions = new List<string>();
            if (!string.IsNullOrEmpty(uploadExtensionsWhitelist))
            {
                var extensions = uploadExtensionsWhitelist.Split(new[] { ',', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                foreach (var ext in extensions)
                {
                    var trimmedExt = ext.Trim();
                    if (!trimmedExt.StartsWith("."))
                    {
                        trimmedExt = "." + trimmedExt;
                    }
                    allowedExtensions.Add(trimmedExt.ToLower());
                }
                UnityEngine.Debug.Log($"[P4 Auto Sync] Upload whitelist: {string.Join(", ", allowedExtensions)}");
            }
            
            // 准备文件列表（过滤 .meta 文件、不存在的文件和不在白名单中的文件）
            var files = new List<string>();
            var filteredCount = 0;
            foreach (var filePath in syncedFiles)
            {
                if (string.IsNullOrEmpty(filePath))
                    continue;
                
                // 跳过 .meta 文件
                if (filePath.EndsWith(".meta"))
                    continue;
                
                // 验证文件是否存在（防止删除的文件被上传）
                var fullPath = Path.Combine(Application.dataPath, filePath.Substring(7));
                if (!File.Exists(fullPath))
                {
                    UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Skip non-existent files: {filePath}");
                    continue;
                }
                
                // 检查扩展名是否在白名单中
                if (allowedExtensions.Count > 0)
                {
                    var fileExtension = Path.GetExtension(filePath).ToLower();
                    if (!allowedExtensions.Contains(fileExtension))
                    {
                        UnityEngine.Debug.Log($"[P4 Auto Sync] Skip non-whitelisted files: {filePath} (extension: {fileExtension})");
                        filteredCount++;
                        continue;
                    }
                }
                
                // 清除只读属性
                ClearReadOnlyAttribute(filePath);
                
                files.Add(filePath);
            }
            
            if (filteredCount > 0)
            {
                UnityEngine.Debug.Log($"[P4 Auto Sync] Whitelist filtering: {filteredCount} non-whitelisted files skipped");
            }
            
            if (files.Count == 0)
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] There are no valid files to upload after filtering (may be deletion operations or .meta files)");
                return;
            }
            
            UnityEngine.Debug.Log($"[P4 Auto Sync] Call ExportFilesAsync to upload {files.Count} files in batches: \n - {string.Join("\n  - ", files)}");
            
            // 使用反射创建 ExportOption
            System.Type exportOptionType = null;
            
            // 遍历所有程序集查找 ExportOption 类型（应该在同一个程序集中）
            var exporterAssembly = exporterType.Assembly;
            UnityEngine.Debug.Log($"[P4 Auto Sync] Looking for ExportOption type in assembly {exporterAssembly.GetName().Name}...");
            
            try
            {
                exportOptionType = exporterAssembly.GetType("ShareCreators.Exporter.ExportOption");
                if (exportOptionType != null)
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] ✓ Find the ExportOption type");
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Error looking up ExportOption: {ex.Message}");
            }
            
            // 如果在同一程序集中找不到，再遍历所有程序集
            if (exportOptionType == null)
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] Find ExportOption... in other assemblies");
                foreach (var assembly in allAssemblies)
                {
                    try
                    {
                        exportOptionType = assembly.GetType("ShareCreators.Exporter.ExportOption");
                        if (exportOptionType != null)
                        {
                            UnityEngine.Debug.Log($"[P4 Auto Sync] ✓ Found ExportOption type in assembly {assembly.GetName().Name}");
                            break;
                        }
                    }
                    catch { }
                }
            }
            
            if (exportOptionType == null)
            {
                UnityEngine.Debug.LogError("[P4 Auto Sync] Unable to find ShareCreators.Exporter.ExportOption type");
                return;
            }
            
            var exportOption = System.Activator.CreateInstance(exportOptionType);
            var exportDependenciesField = exportOptionType.GetField("exportDependencies", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (exportDependenciesField != null)
            {
                exportDependenciesField.SetValue(exportOption, false);
            }
            
            // 调用 ExportFilesAsync 方法
            var exportFilesAsyncMethod = exporterType.GetMethod("ExportFilesAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (exportFilesAsyncMethod == null)
            {
                UnityEngine.Debug.LogError("[P4 Auto Sync] Unable to find ExportFilesAsync method");
                return;
            }
            
            exportFilesAsyncMethod.Invoke(exporter, new object[] { null, files.ToArray(), exportOption });
            
            UnityEngine.Debug.Log($"[P4 Auto Sync] Batch upload task triggered, total {files.Count} files");
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] Failed to upload to server: {ex.Message}\n{ex.StackTrace}");
        }
#else
        UnityEngine.Debug.LogWarning("[P4 Auto Sync] ShareCreators plug-in is not enabled and files cannot be uploaded");
#endif
    }

    private void LogChanges(List<string> changes)
    {
        try
        {
            var logPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", logFilePath));
            var sb = new StringBuilder();
            
            // 统计各类变更数量
            int addedCount = 0;
            int modifiedCount = 0;
            int deletedCount = 0;
            
            var addedFiles = new List<string>();
            var modifiedFiles = new List<string>();
            var deletedFiles = new List<string>();
            
            foreach (var change in changes)
            {
                if (change.StartsWith("[New]"))
                {
                    addedCount++;
                    addedFiles.Add(change.Substring(5).Trim());
                }
                else if (change.StartsWith("[Revise]"))
                {
                    modifiedCount++;
                    modifiedFiles.Add(change.Substring(5).Trim());
                }
                else if (change.StartsWith("[delete]"))
                {
                    deletedCount++;
                    deletedFiles.Add(change.Substring(5).Trim());
                }
            }
            
            sb.AppendLine($"=== Detection time: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"Monitoring path: {monitorPath}");
            sb.AppendLine($"Change statistics: Add {addedCount} | Modify {modifiedCount} | Delete {deletedCount} | Total {changes.Count}");
            sb.AppendLine();
            
            if (addedFiles.Count > 0)
            {
                sb.AppendLine($"【New file】({addedFiles.Count}):");
                foreach (var file in addedFiles)
                {
                    sb.AppendLine($"  + {file}");
                }
                sb.AppendLine();
            }
            
            if (modifiedFiles.Count > 0)
            {
                sb.AppendLine($"【Modify file】({modifiedFiles.Count}):");
                foreach (var file in modifiedFiles)
                {
                    sb.AppendLine($"  * {file}");
                }
                sb.AppendLine();
            }
            
            if (deletedFiles.Count > 0)
            {
                sb.AppendLine($"【Delete file】({deletedFiles.Count}):");
                foreach (var file in deletedFiles)
                {
                    sb.AppendLine($"  - {file}");
                }
                sb.AppendLine();
            }

            File.AppendAllText(logPath, sb.ToString());
            UnityEngine.Debug.Log($"[P4 Auto Sync] Recorded changes to log: Add {addedCount}, Modify {modifiedCount}, Delete {deletedCount}");
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] Failed to write log: {ex.Message}");
        }
    }

    private void OpenLogFile()
    {
        string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", logFilePath));
        if (File.Exists(fullPath))
        {
            Process.Start(fullPath);
            UnityEngine.Debug.Log($"[P4 Auto Sync] Open log file: {fullPath}");
        }
        else
        {
            EditorUtility.DisplayDialog("Hint",
                $"Log file does not exist: \n{fullPath}\n\nChanges may not have been detected yet.",
                "OK");
        }
    }
    
    /// <summary>
    /// 清除文件的只读属性（用于 Perforce 控制的文件）
    /// </summary>
    private void ClearReadOnlyAttribute(string assetPath)
    {
        try
        {
            // 转换 Unity 资源路径为完整的文件系统路径
            string fullPath;
            if (assetPath.StartsWith("Assets/"))
            {
                fullPath = Path.Combine(Application.dataPath, assetPath.Substring(7));
            }
            else if (assetPath.StartsWith("Assets"))
            {
                fullPath = Application.dataPath;
            }
            else
            {
                fullPath = assetPath;
            }
            
            if (File.Exists(fullPath))
            {
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    // 移除只读属性
                    File.SetAttributes(fullPath, attributes & ~FileAttributes.ReadOnly);
                    UnityEngine.Debug.Log($"[P4 Auto Sync] Cleared read-only attribute: {assetPath}");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Failed to clear read-only attributes: {assetPath}, Error: {ex.Message}");
        }
    }
}
