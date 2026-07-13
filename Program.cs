using Google.Apis.Auth.OAuth2;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util;
using Google.Apis.Util.Store;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using static System.Net.Mime.MediaTypeNames;
using static System.Net.WebRequestMethods;
using File = System.IO.File;
using FileG = Google.Apis.Drive.v3.Data.File;
using Timer = System.Threading.Timer;
using WinApp = System.Windows.Forms.Application;

namespace DeskCloudSync
{
    public class FileFolderDesktop
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string SHA { get; set; } = string.Empty;
    }

    public class FolderConfigDesktop
    {
        public List<FileFolderDesktop> FolderDesktop { get; set; } = new List<FileFolderDesktop>();
    }

    public class FileFolderDisk
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string SHA { get; set; } = string.Empty;
        public string FolderId { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    public class FolderConfigDisk
    {
        public List<FileFolderDisk> FolderDisk { get; set; } = new List<FileFolderDisk>();
    }


    class Program
    {
        public static string MainPathFolder = string.Empty;
        public static string FolderCloudId = string.Empty;
        public static string PathKeyCloud = string.Empty;

        public static DriveService service;

        public static FolderConfigDesktop folderConfigDesktop = new FolderConfigDesktop();
        public static FolderConfigDisk folderConfigDisk = new FolderConfigDisk();

        private static Timer timer;

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        private static NotifyIcon trayIcon;
        private static bool isRunning = true;
        private static bool consoleVisible = false;
        private static ToolStripMenuItem consoleMenuItem;

        static async Task Main()
        {
            string jsonPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MainFolder.json");
            string jsonContent = string.Empty;
            if (File.Exists(jsonPath))
            {
                jsonContent = File.ReadAllText(jsonPath);
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent);
                MainPathFolder = config["MainFolder"];
                FolderCloudId = config["CloudId"];
                PathKeyCloud = config["Key"];
            }
            else
            {
                await collectFileEntries(false);
            }

            var handle = GetConsoleWindow();
            ShowWindow(handle, SW_HIDE);
            trayIcon = new NotifyIcon();
            trayIcon.Text = "Мое консольное приложение";
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Visible = true;

            ContextMenuStrip contextMenu = new ContextMenuStrip();

            consoleMenuItem = new ToolStripMenuItem("Показать консоль");
            consoleMenuItem.Click += (s, e) => ToggleConsole();

            ToolStripSeparator separator = new ToolStripSeparator();

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += (s, e) =>
            {
                isRunning = false;
                trayIcon.Visible = false;
                trayIcon.Dispose();
                WinApp.Exit();
            };

            contextMenu.Items.Add(consoleMenuItem);
            contextMenu.Items.Add(separator);
            contextMenu.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = contextMenu;

            trayIcon.DoubleClick += (s, e) => ToggleConsole();
            Thread workerThread = new Thread(BackgroundWork);
            workerThread.IsBackground = true;
            workerThread.Start();
            WinApp.Run();

            //await compareFileShaDesk();
            Console.ReadKey();
        }

        static async void BackgroundWork()
        {
            await ConnectToDisk();
            await checkFilesDesk();
            await checkFilesCloud();
            await compareFileNameCloud();
            await compareFileShaCloud();
            timer = new System.Threading.Timer(async _ => await TimerCallback(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }//Первоначальная проверка файлов
        static void ToggleConsole()
        {
            var handle = GetConsoleWindow();

            if (consoleVisible)
            {
                ShowWindow(handle, SW_HIDE);
                consoleVisible = false;
                consoleMenuItem.Text = "Показать консоль";
            }
            else
            {
                ShowWindow(handle, SW_SHOW);
                consoleVisible = true;
                consoleMenuItem.Text = "Скрыть консоль";
            }
        }//переключение между отображением консоли
        static async Task TimerCallback()
        {
            try
            {
                Console.WriteLine($"\n⏰ [{DateTime.Now:HH:mm:ss}] Запуск синхронизации...");
                await checkFilesDesk();
                await Task.Delay(500);
                await SyncFolderStructureToCloud();
                await compareFileNameDesk();
                Console.WriteLine($"✅ [{DateTime.Now:HH:mm:ss}] Синхронизация завершена");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка в таймере: {ex.Message}");
            }
        }//цикл на проверку  файлов
        static async Task collectFileEntries(bool KeyCloud)
        {
            if(!KeyCloud)
            {
                Console.WriteLine("Укажите путь до основной папки");
                MainPathFolder = Console.ReadLine();
                Console.WriteLine("Укажите Id папки облачного хранилища");
                FolderCloudId = Console.ReadLine();
            }
            Console.WriteLine("Укажите путь до ключа облачного хранилища");
            PathKeyCloud = Console.ReadLine();

            var Datacollect = new { MainFolder = MainPathFolder, CloudId = FolderCloudId, Key = PathKeyCloud };
            string jsonString = JsonSerializer.Serialize(Datacollect);
            string FullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MainFolder.json");
            File.WriteAllText(FullPath, jsonString);
        }//сбор первоначальных ссылок 
        static async Task ConnectToDisk()
        {
            try
            {
                string[] Scopes = { DriveService.Scope.Drive };

                if (!File.Exists(PathKeyCloud))
                {
                    Console.WriteLine($"ОШИБКА: Файл {PathKeyCloud} не найден!");
                    await collectFileEntries(true);
                    ConnectToDisk();
                }

                UserCredential credential;
                using (var stream = new FileStream(PathKeyCloud, FileMode.Open, FileAccess.Read))
                {
                    var secrets = GoogleClientSecrets.Load(stream).Secrets;
                    credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        secrets,
                        Scopes,
                        "user",
                        CancellationToken.None,
                        new FileDataStore("token.json", true)
                    );
                }

                service = new DriveService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential
                });
                Console.WriteLine("Облако успешно подключено");

            }
            catch
            {

            }
        }//подключение к облаку
        static async Task checkFilesDesk()
        {
            folderConfigDesktop.FolderDesktop.Clear();
            folderConfigDesktop.FolderDesktop = new List<FileFolderDesktop>();
            string[] filesFolder = Directory.GetFiles(MainPathFolder, "*", SearchOption.AllDirectories);
            if (filesFolder != null)
            {
                foreach (string file in filesFolder)
                {
                    string fileName = Path.GetFileName(file);

                    using (FileStream stream = File.OpenRead(file))
                    {
                        byte[] hashBytes = SHA256.HashData(stream);
                        string hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                        folderConfigDesktop.FolderDesktop.Add(new FileFolderDesktop
                        {
                            Name = fileName,
                            Path = file,
                            SHA = hashString,
                        });
                    }
                }
            }
        }//Создание списка всех файлов на пк
        static async Task checkFilesCloud()
        {
            folderConfigDisk.FolderDisk = new List<FileFolderDisk>();

            await checkFilesCloudRecursive(FolderCloudId, null, folderConfigDisk.FolderDisk);
        }//Создание списка всех файлов на Облаке
        static async Task checkFilesCloudRecursive(string FolderId, string LastFolder, List<FileFolderDisk> filesList)
        {
            string pageToken = null;
            do
            {
                var listRequest = service.Files.List();
                listRequest.PageSize = 50;
                listRequest.Q = $"'{FolderId}' in parents and trashed = false";
                listRequest.Fields = "nextPageToken, files(id, name, sha256Checksum, mimeType)";
                listRequest.PageToken = pageToken;

                var result = await listRequest.ExecuteAsync();

                if (result.Files != null && result.Files.Count > 0)
                {
                    foreach (var file in result.Files)
                    {
                        var request = service.Files.Get(FolderId);
                        request.Fields = "name";
                        var folder = await request.ExecuteAsync();

                        string FullPath = string.Empty;
                        if (FolderId != FolderCloudId)
                        {
                            FullPath = string.IsNullOrEmpty(LastFolder)? folder.Name : Path.Combine(LastFolder, folder.Name);
                        }

                        filesList.Add(new FileFolderDisk
                        {
                            Id = file.Id,
                            Name = file.Name,
                            SHA = file.Sha256Checksum,
                            FolderId = FolderId,
                            Path = FullPath
                        });

                        if (file.MimeType == "application/vnd.google-apps.folder")
                        {
                            await checkFilesCloudRecursive(file.Id, FullPath, filesList);
                        }
                    }
                }
                pageToken = result.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));
        }//рекурсивный метод получения всех файлов с диска
        static async Task compareFileNameCloud()
        {
            var newFiles = folderConfigDisk.FolderDisk.Where(p => !folderConfigDesktop.FolderDesktop.Any(d => d.Name == p.Name)).ToList();

            foreach (var file in newFiles)
            {
                await DownloadFilefromCloud(file.Id, file.SHA, file.FolderId, file.Path, false);
            }
        }//Нахождения новых файлов на облаке за счет его имени
        static async Task compareFileNameDesk()
        {
            var newFiles = folderConfigDesktop.FolderDesktop.Where(p => !folderConfigDisk.FolderDisk.Any(d => d.Name == p.Name)).ToList();

            foreach (var file in newFiles)
            {
                InstallFilefromDesk(file.Path, file.Name);
                Console.WriteLine($"Новый файл на компьютере: {file.Name} в папке {file.Path}");
            }
        }//Нахождения новых файлов на пк за счет его имени
        static async Task SyncFolderStructureToCloud()//Нахождение новых папок на пк
        {
            string[] filesFolder = Directory.GetFiles(MainPathFolder, "*", SearchOption.AllDirectories);
            foreach (var file in filesFolder)
            {
                var uniquePaths = folderConfigDesktop.FolderDesktop.Select(f => Path.GetDirectoryName(f.Path)).Distinct().ToList();
                foreach (var fullPath in uniquePaths)
                {
                    string relativePath = Path.GetRelativePath(MainPathFolder, fullPath);
                    if (relativePath == "." || string.IsNullOrEmpty(relativePath))
                    {
                        continue;
                    }

                    string[] folderParts = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    string currentParentId = FolderCloudId;

                    foreach (string folderName in folderParts)
                    {
                        if (string.IsNullOrEmpty(folderName))
                        {
                            continue;
                        }

                        var existingFolder = folderConfigDisk.FolderDisk.FirstOrDefault(f => f.Name == folderName && f.FolderId == currentParentId);

                        if (existingFolder != null)
                        {
                            currentParentId = existingFolder.Id;
                        }
                        else
                        {
                            string newFolderId = await CreateNewFolderCloud(folderName, currentParentId);
                            if (!string.IsNullOrEmpty(newFolderId))
                            {
                                currentParentId = newFolderId;
                            }
                        }
                    }
                }
            }
        }
        static async Task DownloadFilefromCloud(string fileId, string FileSHA, string FileFolderId, string FullPathFileCloud, bool UpdateFile)
        {
            try
            {
                var request = service.Files.Get(fileId);
                var fileMetadata = await request.ExecuteAsync();
                string fileName = fileMetadata.Name;
                string fullPath = Path.Combine(MainPathFolder, fileName);

                if (FileFolderId != FolderCloudId)
                {
                    fullPath = Path.Combine(MainPathFolder, FullPathFileCloud, fileName);
                }
                if (FileSHA != null)
                {
                    if(UpdateFile)
                    {
                        FileInfo fileInf = new FileInfo(fullPath);
                        fileInf.Delete();
                    }
                    using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
                    {
                        var result = await request.DownloadAsync(stream);
                    }
                }
                else
                {
                    Directory.CreateDirectory(fullPath);
                }
                await checkFilesDesk();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
        }//Скачивание новых файлов с диска на пк
        static async Task compareFileShaCloud()
        {
            var changedFiles = folderConfigDisk.FolderDisk.Where(diskFile => folderConfigDesktop.FolderDesktop.Any(desktopFile => desktopFile.Name == diskFile.Name && desktopFile.SHA != diskFile.SHA)).ToList();
            foreach (var file in changedFiles)
            {
                await DownloadFilefromCloud(file.Id, file.SHA, file.FolderId, file.Path, true);
                Console.WriteLine($"файл изменился на диске, его надо обновить на пк {file.Name}");
            }
        }//Нахождение изменений файлов на диске за счет вычисления хеша файлов пк и файлов облака 
        static async Task InstallFilefromDesk(string filePath, string filename)
        {
            try
            {
                string FolderId = string.Empty;
                string folderPath = Path.GetDirectoryName(filePath);
                string folderName = folderPath.Replace(MainPathFolder, "").Trim(Path.DirectorySeparatorChar);
                string[] FolderSplit = folderName.Split(Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(folderName))
                {
                    FolderId = FolderCloudId;
                }
                else
                {
                    var folder = folderConfigDisk.FolderDisk.FirstOrDefault(f => f.Name == FolderSplit[FolderSplit.Length -1]);
                    FolderId = folder.Id;
                }

                var fileMetadata = new FileG()
                {
                    Name = Path.GetFileName(filename),
                    MimeType = "application/octet-stream",
                    Parents = new List<string> { FolderId }
                };

                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    var request = service.Files.Create(fileMetadata, fileStream, "application/octet-stream");
                    await request.UploadAsync();

                    string fileId = request.ResponseBody?.Id;

                    using (var sha256 = SHA256.Create())
                    {
                        fileStream.Position = 0;
                        byte[] hashBytes = sha256.ComputeHash(fileStream);
                        string hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                        folderConfigDisk.FolderDisk.Add(new FileFolderDisk
                        {
                            Name = Path.GetFileName(filePath),
                            Id = fileId,
                            SHA = hash,
                            FolderId = FolderId,
                            Path = folderName
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка загрузки: {ex.Message}");
            }
        }//Установка новых файлов пк на облако 
        static async Task<string> CreateNewFolderCloud(string folderName, string parentFolderId)
        {
            try
            {
                var fileMetadata = new Google.Apis.Drive.v3.Data.File()
                {
                    Name = folderName,
                    MimeType = "application/vnd.google-apps.folder",
                    Parents = new List<string> { parentFolderId }
                };

                var request = service.Files.Create(fileMetadata);
                request.Fields = "id";

                var folder = await request.ExecuteAsync();
                string newFolderId = folder.Id;

                folderConfigDisk.FolderDisk.Add(new FileFolderDisk
                {
                    Id = newFolderId,
                    Name = folderName,
                    FolderId = parentFolderId
                });
                return newFolderId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка создания папки: {ex.Message}");
                return null;
            }
        } //Создание новых папок на облаке
        static async Task compareFileShaDesk()//Нахождение изменений файлов на пк за счет вычисления хеша файлов облака и файлов пк 
        {
            var changedFiles = folderConfigDesktop.FolderDesktop.Where(deskFile => folderConfigDisk.FolderDisk.Any(diskFile => diskFile.Name == deskFile.Name && diskFile.SHA != deskFile.SHA)).ToList();
            foreach (var file in changedFiles)
            {
                await UpdateFile(file.Path, file.Name);
                Console.WriteLine($"файл изменился на пк, его надо обновить на диске {file.Name}");
            }
        }
        static async Task UpdateFile(string filePath, string FileName)
        {
            //try
            //{
            //    string currentParentName = Path.GetFileName(Path.GetDirectoryName(filePath));
            //    var existingFolder = folderConfigDisk.FolderDisk.FirstOrDefault(z => z.Name == currentParentName);
            //    var fileMetadata = new FileG()
            //    {
            //        Name = Path.GetFileName(filePath),
            //        MimeType = "application/octet-stream",
            //        Parents = new List<string> { existingFolder.Id }
            //    };
            //    using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            //    {
            //        var request = service.Files.Update(fileMetadata, FileName, fileStream, "application / octet - stream");
            //        request.Fields = "id, name, version, webViewLink, size, modifiedTime";

            //        var result = await request.UploadAsync();

            //        if (result.Status == UploadStatus.Completed)
            //        {
            //            var updatedFile = request.ResponseBody;
            //        }
            //        else
            //        {
            //            Console.WriteLine($" Ошибка обновления: {result.Exception?.Message}");
            //        }
            //    }
            //}
            //catch (Exception ex)
            //{
            //    Console.WriteLine($"Ошибка обновления: {ex.Message}");
            //}
        }
    }
}
