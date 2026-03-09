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
public class PerforceAutoSyncMonitor : EditorWindow
{
    private int checkInterval = 1800;
    private string monitorPath = "Assets";  // Unity 项目路径，如 "Assets" 或 "Assets/Scripts"
    private string logFilePath = "changes_log.txt";
    private bool isMonitoring;
    private string statusMessage = "未启动";
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

    [MenuItem("Window/Perforce Auto Sync Monitor")]
    public static void ShowWindow()
    {
        GetWindow<PerforceAutoSyncMonitor>("P4 Auto Sync");
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
        GUILayout.Label("Perforce 自动同步监控", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // Perforce 配置区域
        EditorGUILayout.LabelField("Perforce 配置", EditorStyles.boldLabel);
        EditorGUI.BeginDisabledGroup(isMonitoring);
        
        p4ExePath = EditorGUILayout.TextField("P4 执行文件路径", p4ExePath);
        EditorGUILayout.HelpBox("通常是 'p4' 或 'p4.exe'，如果不在 PATH 中，请填写完整路径", MessageType.Info);
        
        p4Port = EditorGUILayout.TextField("P4PORT (服务器)", p4Port);
        p4User = EditorGUILayout.TextField("P4USER (用户名)", p4User);
        p4Password = EditorGUILayout.PasswordField("P4PASSWD (密码)", p4Password);
        p4Client = EditorGUILayout.TextField("P4CLIENT (Workspace)", p4Client);
        EditorGUILayout.HelpBox("密码用于 session 过期时自动登录。留空则需要手动执行 'p4 login'", MessageType.Info);
        
        EditorGUILayout.BeginHorizontal();
        // if (GUILayout.Button("查找 p4.exe"))
        // {
        //     FindP4Executable();
        //     Repaint();
        // }
        if (GUILayout.Button("自动检测配置"))
        {
            LoadP4Settings();
            Repaint();
        }
        if (GUILayout.Button("保存配置"))
        {
            SaveP4Settings(true);
        }
        if (GUILayout.Button("测试连接"))
        {
            TestP4Connection();
        }
        if (GUILayout.Button("登录 P4"))
        {
            LoginToP4();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUI.EndDisabledGroup();
        
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("监控设置", EditorStyles.boldLabel);
        
        EditorGUI.BeginDisabledGroup(isMonitoring);
        checkInterval = EditorGUILayout.IntSlider("检查间隔（秒）", checkInterval, 20, 3600);
        monitorPath = EditorGUILayout.TextField("监控路径", monitorPath);
        EditorGUILayout.HelpBox("支持配置多个路径，用分号(;)分隔。例如: Assets/Scripts;Assets/Prefabs", MessageType.Info);
        logFilePath = EditorGUILayout.TextField("日志文件", logFilePath);
        autoSync = EditorGUILayout.Toggle("自动同步文件", autoSync);
        EditorGUILayout.HelpBox(
            autoSync ? "✓ 检测到变更后自动执行 p4 sync 更新文件" : "❌ 只记录变更，不自动同步文件",
            autoSync ? MessageType.Info : MessageType.Warning);
        
        EditorGUI.BeginDisabledGroup(!autoSync);
        autoExport = EditorGUILayout.Toggle("自动上传到服务器", autoExport);
        EditorGUILayout.HelpBox(
            autoExport ? "✓ 同步后自动调用 ShareCreators 导出插件上传变更文件" : "❌ 只同步，不上传到服务器",
            autoExport ? MessageType.Info : MessageType.Warning);
        
        EditorGUI.BeginDisabledGroup(!autoExport);
        EditorGUILayout.LabelField("上传文件扩展名白名单", EditorStyles.boldLabel);
        uploadExtensionsWhitelist = EditorGUILayout.TextField("允许的扩展名", uploadExtensionsWhitelist);
        EditorGUILayout.HelpBox(
            "只有这些扩展名的文件会上传到SC服务器\n" +
            "多个扩展名用逗号分隔，如: .prefab,.fbx,.tga,.png\n" +
            "当前白名单: " + (string.IsNullOrEmpty(uploadExtensionsWhitelist) ? "全部允许" : uploadExtensionsWhitelist),
            MessageType.Info);
        EditorGUI.EndDisabledGroup();
        
        EditorGUI.EndDisabledGroup();
        
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(isMonitoring ? "停止监控" : "启动监控"))
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

        if (GUILayout.Button("手动检查一次"))
        {
            CheckForRemoteChanges();
        }

        if (GUILayout.Button("打开变更日志"))
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
            "使用说明：\n" +
            "• 监控路径：Unity 项目路径，支持多个路径用分号分隔\n" +
            "  示例: Assets 或 Assets/Scripts;Assets/Prefabs\n" +
            "• 日志文件：变更记录保存位置（相对于项目根目录）\n" +
            "• 自动同步：检测到变更后自动执行 p4 sync 更新文件\n" +
            "• 自动上传：同步后自动上传变更文件到 ShareCreators 服务器\n" +
            "• 密码配置：用于 session 过期时自动登录（可选）\n\n" +
            "注意：\n" +
            "• Perforce 使用 ticket 认证，有效期约 12 小时\n" +
            "• 首次使用或 ticket 过期时需要登录\n" +
            "• 配置密码后可自动重新登录",
            MessageType.None);
    }

    private void StartMonitoring()
    {
        // 检查 P4 配置是否完整
        if (string.IsNullOrEmpty(p4Port) || string.IsNullOrEmpty(p4User) || string.IsNullOrEmpty(p4Client))
        {
            var errorMsg = "Perforce 配置不完整！\n\n" +
                $"P4PORT: {(string.IsNullOrEmpty(p4Port) ? "❌ 未设置" : "✓ " + p4Port)}\n" +
                $"P4USER: {(string.IsNullOrEmpty(p4User) ? "❌ 未设置" : "✓ " + p4User)}\n" +
                $"P4CLIENT: {(string.IsNullOrEmpty(p4Client) ? "❌ 未设置" : "✓ " + p4Client)}\n\n" +
                "请在窗口上方填写 Perforce 配置，\n" +
                "或点击「自动检测配置」按钮。";
            
            EditorUtility.DisplayDialog("配置不完整", errorMsg, "确定");
            return;
        }
        
        // 测试 p4 命令是否可用
        if (!TestP4Command())
        {
            var errorMsg = "无法执行 p4 命令。\n\n" +
                "当前配置：\n" +
                $"P4PORT: {p4Port}\n" +
                $"P4USER: {p4User}\n" +
                $"P4CLIENT: {p4Client}\n\n" +
                "请确保：\n" +
                "1. 已安装 Perforce 客户端 (p4.exe)\n" +
                "2. p4.exe 在系统 PATH 中\n" +
                "3. 配置信息正确\n" +
                "4. 已运行 'p4 login' 登录\n\n" +
                "建议点击「测试连接」按钮查看详细信息。";
            
            EditorUtility.DisplayDialog("P4 命令执行失败", errorMsg, "确定");
            return;
        }

        isMonitoring = true;
        lastCheckTime = System.DateTime.Now;
        changesDetected = 0;
        statusMessage = $"监控已启动\n间隔: {checkInterval}秒\n路径: {monitorPath}\n日志: {logFilePath}";
        lastError = string.Empty;

        UnityEngine.Debug.Log($"[P4 Auto Sync] 启动监控: interval={checkInterval}s, path={monitorPath}");
        
        // 立即执行一次检查
        CheckForRemoteChanges();
    }

    private void StopMonitoring()
    {
        isMonitoring = false;
        statusMessage = "监控已停止";
        UnityEngine.Debug.Log("[P4 Auto Sync] 停止监控");
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
            
        UnityEngine.Debug.Log($"[P4 Auto Sync] P4 配置已加载: EXE={p4ExePath}, PORT={p4Port}, USER={p4User}, CLIENT={p4Client}");
    }
    
    private void FindP4Executable()
    {
        UnityEngine.Debug.Log("[P4 Auto Sync] 查找 p4.exe...");
        
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
                UnityEngine.Debug.Log($"[P4 Auto Sync] 找到 p4.exe: {p4ExePath}");
                EditorUtility.DisplayDialog("成功", $"找到 p4.exe:\n{p4ExePath}", "确定");
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
                        UnityEngine.Debug.Log($"[P4 Auto Sync] 通过 where 命令找到 p4: {p4ExePath}");
                        EditorUtility.DisplayDialog("成功", $"找到 p4.exe:\n{p4ExePath}", "确定");
                        return;
                    }
                }
            }
        }
        catch
        {
            // 忽略错误
        }
        
        EditorUtility.DisplayDialog("未找到 p4.exe", 
            "未找到 p4.exe。\n\n" +
            "请手动输入 p4.exe 的完整路径，\n" +
            "或确保 p4.exe 在系统 PATH 中。\n\n" +
            "常见位置：\n" +
            "C:\\Program Files\\Perforce\\p4.exe", 
            "确定");
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
        
        UnityEngine.Debug.Log($"[P4 Auto Sync] P4 配置已保存: EXE={p4ExePath}, PORT={p4Port}, USER={p4User}, CLIENT={p4Client}");
        if (showTips)
        {
            EditorUtility.DisplayDialog("成功", "Perforce 配置已保存！", "确定");
        }
    }
    
    private void TestP4Connection()
    {
        UnityEngine.Debug.Log("[P4 Auto Sync] 测试 P4 连接...");
        
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
                    UnityEngine.Debug.Log($"[P4 Auto Sync] P4 连接成功!\n{output}");
                    
                    // 从输出中提取关键信息
                    var info = new StringBuilder();
                    info.AppendLine("连接成功！");
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
                    
                    EditorUtility.DisplayDialog("P4 连接测试", info.ToString(), "确定");
                }
                else
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] P4 连接失败!\n错误: {error}");
                    EditorUtility.DisplayDialog("P4 连接测试失败", 
                        $"无法连接到 Perforce 服务器。\n\n错误信息:\n{error}\n\n请检查配置是否正确。", 
                        "确定");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] P4 连接测试失败: {ex.Message}");
            EditorUtility.DisplayDialog("P4 连接测试失败", 
                $"执行 p4 命令失败。\n\n错误:\n{ex.Message}\n\n请确保 p4.exe 已安装并在 PATH 中。", 
                "确定");
        }
    }
    
    private void LoginToP4()
    {
        UnityEngine.Debug.Log("[P4 Auto Sync] 登录到 Perforce...");
        
        if (string.IsNullOrEmpty(p4Password))
        {
            EditorUtility.DisplayDialog("需要密码", 
                "请先在上方输入 Perforce 密码，\n然后点击「保存配置」和「登录 P4」。\n\n" +
                "或者在命令行手动执行：\np4 login", 
                "确定");
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
                    UnityEngine.Debug.Log($"[P4 Auto Sync] 登录成功!\n{output}");
                    EditorUtility.DisplayDialog("登录成功", 
                        "已成功登录到 Perforce 服务器！\n\n" +
                        "Session ticket 已生成，有效期通常为 12 小时。", 
                        "确定");
                }
                else
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] 登录失败!\n错误: {error}");
                    EditorUtility.DisplayDialog("登录失败", 
                        $"无法登录到 Perforce 服务器。\n\n错误信息:\n{error}\n\n请检查用户名和密码是否正确。", 
                        "确定");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] 登录失败: {ex.Message}");
            EditorUtility.DisplayDialog("登录失败", 
                $"执行 p4 login 命令失败。\n\n错误:\n{ex.Message}", 
                "确定");
        }
    }
    
    private bool TryAutoLogin()
    {
        if (string.IsNullOrEmpty(p4Password))
        {
            UnityEngine.Debug.LogWarning("[P4 Auto Sync] 未配置密码，无法自动登录");
            return false;
        }
        
        UnityEngine.Debug.Log("[P4 Auto Sync] Session 过期，尝试自动登录...");
        
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
                    UnityEngine.Debug.Log("[P4 Auto Sync] 自动登录成功！");
                    return true;
                }
                else
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] 自动登录失败: {error}");
                    return false;
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] 自动登录异常: {ex.Message}");
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
                
                UnityEngine.Debug.Log($"[P4 Auto Sync] p4 info 输出:\n{output}");
                if (!string.IsNullOrEmpty(error))
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] p4 info 错误:\n{error}");
                }
                
                return process.ExitCode == 0;
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] p4 命令测试失败: {ex.Message}");
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
        UnityEngine.Debug.Log($"[P4 Auto Sync] 检查远端变更: {monitorPath}");

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
                    
                UnityEngine.Debug.Log($"[P4 Auto Sync] 检查路径: {trimmedPath}");
                
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
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] 无法获取 depot 路径: {fullPath}");
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
                
                statusMessage = $"发现 {allChangedFiles.Count} 个远端变更\n" +
                               (autoSync ? "已自动同步文件\n" : "") +
                               (autoExport && autoSync ? "已上传到服务器\n" : "") +
                               $"总计: {changesDetected} 个\n" +
                               $"最后检查: {System.DateTime.Now:HH:mm:ss}";
                
                UnityEngine.Debug.Log($"[P4 Auto Sync] 发现 {allChangedFiles.Count} 个远端变更" + 
                    (autoSync ? "，已同步" : "") + 
                    (autoExport && autoSync ? "，已上传" : ""));
            }
            else
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] 没有发现远端变更");
                statusMessage = $"没有远端变更\n最后检查: {System.DateTime.Now:HH:mm:ss}";
            }

            lastError = string.Empty;
        }
        catch (System.Exception ex)
        {
            lastError = ex.Message;
            UnityEngine.Debug.LogError($"[P4 Auto Sync] 检查失败: {ex.Message}\n{ex.StackTrace}");
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
                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] Session 已过期，尝试自动登录...");
                        
                        if (TryAutoLogin())
                        {
                            // 登录成功，重试命令
                            UnityEngine.Debug.Log("[P4 Auto Sync] 自动登录成功，重试获取 depot 路径...");
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
                            lastError = "Perforce session 已过期，请点击「登录 P4」按钮重新登录";
                            EditorUtility.DisplayDialog("Session 过期", 
                                "Perforce session 已过期。\n\n" +
                                (string.IsNullOrEmpty(p4Password) 
                                    ? "请点击「登录 P4」按钮重新登录，\n或在命令行执行: p4 login"
                                    : "自动登录失败，请检查密码是否正确，\n然后点击「登录 P4」按钮。"), 
                                "确定");
                            StopMonitoring();
                            return null;
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(error))
                    {
                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] p4 where 警告: {error}");
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
            UnityEngine.Debug.LogError($"[P4 Auto Sync] 获取 depot 路径失败: {ex.Message}");
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
                    UnityEngine.Debug.LogWarning($"[P4 Auto Sync] p4 sync -n 警告: {error}");
                }

                if (!string.IsNullOrEmpty(output))
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] p4 sync -n 输出:\n{output}");
                    
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
                                changeType = "新增";
                            else if (trimmedLine.Contains(" - updating "))
                                changeType = "修改";
                            else if (trimmedLine.Contains(" - deleted as "))
                                changeType = "删除";
                            
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
            UnityEngine.Debug.LogError($"[P4 Auto Sync] 检查服务器变更失败: {ex.Message}");
        }

        return changedFiles;
    }

    private List<string> SyncChangedFiles(string depotPath)
    {
        var syncedFiles = new List<string>();
        
        try
        {
            UnityEngine.Debug.Log($"[P4 Auto Sync] 开始同步文件: {depotPath}/...");
            
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
                                    UnityEngine.Debug.Log($"[P4 Auto Sync] 检测到删除操作: {depotFile}");
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
                UnityEngine.Debug.Log("[P4 Auto Sync] 没有文件需要同步");
                return syncedFiles;
            }
            
            UnityEngine.Debug.Log($"[P4 Auto Sync] 准备同步 {filesToSync.Count} 个文件，删除 {filesToDelete.Count} 个文件");
            
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
                        UnityEngine.Debug.Log($"[P4 Auto Sync] 删除操作完成:\n{deleteOutput}");
                    }
                    if (!string.IsNullOrEmpty(deleteError) && !deleteError.Contains("file(s) up-to-date"))
                    {
                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 删除操作警告: {deleteError}");
                    }
                }
            }
            
            // 2. 对新增/修改的文件执行 p4 sync -f（强制同步，解决 "Can't clobber writable file" 错误）
            if (filesToSync.Count == 0)
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] 没有文件需要添加或更新");
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
                    UnityEngine.Debug.Log($"[P4 Auto Sync] 同步成功:\n{output}");
                    
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
                                UnityEngine.Debug.Log($"[P4 Auto Sync] 跳过已删除的文件: {trimmedLine}");
                                continue;
                            }
                            
                            // 跳过已打开但未改变的文件
                            if (trimmedLine.Contains(" - is opened and not being changed"))
                            {
                                UnityEngine.Debug.Log($"[P4 Auto Sync] 跳过已打开但未改变的文件: {trimmedLine}");
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
                                    UnityEngine.Debug.Log($"[P4 Auto Sync] 解析 'added as' 格式: {localPath}");
                                }
                            }
                            // 尝试匹配 "- updating" 格式
                            else if (trimmedLine.Contains(" - updating "))
                            {
                                var parts = trimmedLine.Split(new[] { " - updating " }, System.StringSplitOptions.None);
                                if (parts.Length >= 2)
                                {
                                    localPath = parts[1].Trim();
                                    UnityEngine.Debug.Log($"[P4 Auto Sync] 解析 'updating' 格式: {localPath}");
                                }
                            }
                            // 尝试匹配 "- refreshing" 格式
                            else if (trimmedLine.Contains(" - refreshing "))
                            {
                                var parts = trimmedLine.Split(new[] { " - refreshing " }, System.StringSplitOptions.None);
                                if (parts.Length >= 2)
                                {
                                    localPath = parts[1].Trim();
                                    UnityEngine.Debug.Log($"[P4 Auto Sync] 解析 'refreshing' 格式: {localPath}");
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
                                            UnityEngine.Debug.Log($"[P4 Auto Sync] 已添加同步文件到列表: {assetPath}");
                                        }
                                        else
                                        {
                                            UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 文件不存在，跳过: {assetPath} (可能是删除操作)");
                                        }
                                    }
                                    else
                                    {
                                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 转换为 Asset 路径失败: {localPath}");
                                    }
                                }
                                else
                                {
                                    UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 路径格式无效（缺少盘符）: {localPath}");
                                }
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(error) && !error.Contains("file(s) up-to-date"))
                {
                    UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 同步警告: {error}");
                }
                
                if (process.ExitCode == 0)
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] 文件已同步到最新版本，共 {syncedFiles.Count} 个可上传文件");
                    // 刷新 Unity 资源数据库
                    AssetDatabase.Refresh();
                }
                else
                {
                    UnityEngine.Debug.LogError($"[P4 Auto Sync] 同步失败，退出码: {process.ExitCode}");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] 同步文件失败: {ex.Message}");
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
            UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 转换路径失败: {localPath}, 错误: {ex.Message}");
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
                UnityEngine.Debug.Log("[P4 Auto Sync] 没有需要上传的文件");
                return;
            }
            
            UnityEngine.Debug.Log($"[P4 Auto Sync] 开始上传 {syncedFiles.Count} 个文件到 ShareCreators 服务器...");
            
            // 使用反射获取 ExporterScriptableObject 类型和实例
            System.Type exporterType = null;
            
            // 遍历所有程序集查找类型
            UnityEngine.Debug.Log("[P4 Auto Sync] 开始查找 ExporterScriptableObject 类型...");
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
                    UnityEngine.Debug.Log($"[P4 Auto Sync] 正在检查程序集: {assemblyName}");
                    
                    try
                    {
                        exporterType = assembly.GetType("ShareCreators.Exporter.ExporterScriptableObject");
                        if (exporterType != null)
                        {
                            UnityEngine.Debug.Log($"[P4 Auto Sync] ✓ 在程序集 {assemblyName} 中找到 ExporterScriptableObject 类型");
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 检查程序集 {assemblyName} 时出错: {ex.Message}");
                    }
                }
            }
            
            if (exporterType == null)
            {
                // 列出所有可能相关的程序集和类型，帮助调试
                UnityEngine.Debug.LogError("[P4 Auto Sync] ❌ 无法找到 ShareCreators.Exporter.ExporterScriptableObject 类型");
                UnityEngine.Debug.Log("[P4 Auto Sync] 可用的程序集列表（ShareCreator 相关）：");
                
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
                                UnityEngine.Debug.Log($"    包含 {shareCreatorTypes.Count} 个 ShareCreators 类型:");
                                foreach (var typeName in shareCreatorTypes)
                                {
                                    UnityEngine.Debug.Log($"      → {typeName}");
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            UnityEngine.Debug.LogWarning($"    无法列出类型: {ex.Message}");
                        }
                    }
                }
                
                if (!foundShareCreatorAssembly)
                {
                    UnityEngine.Debug.LogError("[P4 Auto Sync] ⚠️ 没有找到任何 ShareCreator 相关的程序集！");
                    UnityEngine.Debug.Log("[P4 Auto Sync] 提示：请确保 ShareCreators 插件已正确安装在 Unity 项目中");
                }
                
                return;
            }
            
            // 调用 GetInstance() 静态方法
            var getInstanceMethod = exporterType.GetMethod("GetInstance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (getInstanceMethod == null)
            {
                UnityEngine.Debug.LogError("[P4 Auto Sync] 无法找到 GetInstance 方法");
                return;
            }
            
            var exporter = getInstanceMethod.Invoke(null, null);
            if (exporter == null)
            {
                UnityEngine.Debug.LogError("[P4 Auto Sync] 无法获取 ExporterScriptableObject 实例");
                return;
            }
            
            // 检查 IsExporting 属性
            var isExportingProperty = exporterType.GetProperty("IsExporting", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (isExportingProperty != null)
            {
                var isExporting = (bool)isExportingProperty.GetValue(exporter);
                if (isExporting)
                {
                    UnityEngine.Debug.LogWarning("[P4 Auto Sync] ShareCreators 正在导出其他文件，跳过本次上传");
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
                UnityEngine.Debug.Log($"[P4 Auto Sync] 上传白名单: {string.Join(", ", allowedExtensions)}");
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
                    UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 跳过不存在的文件: {filePath}");
                    continue;
                }
                
                // 检查扩展名是否在白名单中
                if (allowedExtensions.Count > 0)
                {
                    var fileExtension = Path.GetExtension(filePath).ToLower();
                    if (!allowedExtensions.Contains(fileExtension))
                    {
                        UnityEngine.Debug.Log($"[P4 Auto Sync] 跳过非白名单文件: {filePath} (扩展名: {fileExtension})");
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
                UnityEngine.Debug.Log($"[P4 Auto Sync] 白名单过滤: 跳过了 {filteredCount} 个非白名单文件");
            }
            
            if (files.Count == 0)
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] 过滤后没有有效文件需要上传（可能都是删除操作或 .meta 文件）");
                return;
            }
            
            UnityEngine.Debug.Log($"[P4 Auto Sync] 调用 ExportFilesAsync 批量上传 {files.Count} 个文件:\n  - {string.Join("\n  - ", files)}");
            
            // 使用反射创建 ExportOption
            System.Type exportOptionType = null;
            
            // 遍历所有程序集查找 ExportOption 类型（应该在同一个程序集中）
            var exporterAssembly = exporterType.Assembly;
            UnityEngine.Debug.Log($"[P4 Auto Sync] 在程序集 {exporterAssembly.GetName().Name} 中查找 ExportOption 类型...");
            
            try
            {
                exportOptionType = exporterAssembly.GetType("ShareCreators.Exporter.ExportOption");
                if (exportOptionType != null)
                {
                    UnityEngine.Debug.Log($"[P4 Auto Sync] ✓ 找到 ExportOption 类型");
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 查找 ExportOption 时出错: {ex.Message}");
            }
            
            // 如果在同一程序集中找不到，再遍历所有程序集
            if (exportOptionType == null)
            {
                UnityEngine.Debug.Log("[P4 Auto Sync] 在其他程序集中查找 ExportOption...");
                foreach (var assembly in allAssemblies)
                {
                    try
                    {
                        exportOptionType = assembly.GetType("ShareCreators.Exporter.ExportOption");
                        if (exportOptionType != null)
                        {
                            UnityEngine.Debug.Log($"[P4 Auto Sync] ✓ 在程序集 {assembly.GetName().Name} 中找到 ExportOption 类型");
                            break;
                        }
                    }
                    catch { }
                }
            }
            
            if (exportOptionType == null)
            {
                UnityEngine.Debug.LogError("[P4 Auto Sync] 无法找到 ShareCreators.Exporter.ExportOption 类型");
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
                UnityEngine.Debug.LogError("[P4 Auto Sync] 无法找到 ExportFilesAsync 方法");
                return;
            }
            
            exportFilesAsyncMethod.Invoke(exporter, new object[] { null, files.ToArray(), exportOption });
            
            UnityEngine.Debug.Log($"[P4 Auto Sync] 已触发批量上传任务，共 {files.Count} 个文件");
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] 上传到服务器失败: {ex.Message}\n{ex.StackTrace}");
        }
#else
        UnityEngine.Debug.LogWarning("[P4 Auto Sync] ShareCreators 插件未启用，无法上传文件");
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
                if (change.StartsWith("[新增]"))
                {
                    addedCount++;
                    addedFiles.Add(change.Substring(5).Trim());
                }
                else if (change.StartsWith("[修改]"))
                {
                    modifiedCount++;
                    modifiedFiles.Add(change.Substring(5).Trim());
                }
                else if (change.StartsWith("[删除]"))
                {
                    deletedCount++;
                    deletedFiles.Add(change.Substring(5).Trim());
                }
            }
            
            sb.AppendLine($"=== 检测时间: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"监控路径: {monitorPath}");
            sb.AppendLine($"变更统计: 新增 {addedCount} | 修改 {modifiedCount} | 删除 {deletedCount} | 合计 {changes.Count}");
            sb.AppendLine();
            
            if (addedFiles.Count > 0)
            {
                sb.AppendLine($"【新增文件】({addedFiles.Count}):");
                foreach (var file in addedFiles)
                {
                    sb.AppendLine($"  + {file}");
                }
                sb.AppendLine();
            }
            
            if (modifiedFiles.Count > 0)
            {
                sb.AppendLine($"【修改文件】({modifiedFiles.Count}):");
                foreach (var file in modifiedFiles)
                {
                    sb.AppendLine($"  * {file}");
                }
                sb.AppendLine();
            }
            
            if (deletedFiles.Count > 0)
            {
                sb.AppendLine($"【删除文件】({deletedFiles.Count}):");
                foreach (var file in deletedFiles)
                {
                    sb.AppendLine($"  - {file}");
                }
                sb.AppendLine();
            }

            File.AppendAllText(logPath, sb.ToString());
            UnityEngine.Debug.Log($"[P4 Auto Sync] 已记录变更到日志: 新增 {addedCount}, 修改 {modifiedCount}, 删除 {deletedCount}");
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[P4 Auto Sync] 写入日志失败: {ex.Message}");
        }
    }

    private void OpenLogFile()
    {
        string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", logFilePath));
        if (File.Exists(fullPath))
        {
            Process.Start(fullPath);
            UnityEngine.Debug.Log($"[P4 Auto Sync] 打开日志文件: {fullPath}");
        }
        else
        {
            EditorUtility.DisplayDialog("提示",
                $"日志文件不存在:\n{fullPath}\n\n可能还没有检测到变更。",
                "确定");
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
                    UnityEngine.Debug.Log($"[P4 Auto Sync] 已清除只读属性: {assetPath}");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[P4 Auto Sync] 清除只读属性失败: {assetPath}, 错误: {ex.Message}");
        }
    }
}
