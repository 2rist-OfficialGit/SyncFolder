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
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using static System.Net.Mime.MediaTypeNames;
using static System.Net.WebRequestMethods;
using File = System.IO.File;
using FileG = Google.Apis.Drive.v3.Data.File;

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
            await ConnectToDisk();
            await checkFilesDesk();
            await checkFilesCloud();
            await compareFileNameCloud();
            await compareFileNameDesk();
            await compareFileShaCloud();
            Console.ReadKey();
        }

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
        }
        static async Task ConnectToDisk()
        {
            try
            {
                string[] Scopes = { DriveService.Scope.Drive };

                if (!File.Exists(PathKeyCloud))
                {
                    Console.WriteLine($"ОШИБКА: Файл {PathKeyCloud} не найден!");
                    collectFileEntries(true);
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

            static async Task checkFilesDeskRecursively()
            {

            }

            static async Task checkFilesCloudRecursively()
            {

            }
        }
        static async Task checkFilesDesk()
        {
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
        }
        static async Task checkFilesCloud()
        {
            folderConfigDisk.FolderDisk = new List<FileFolderDisk>();

            await checkFilesCloudRecursive(FolderCloudId, null, folderConfigDisk.FolderDisk);
        }
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
        }
        static async Task compareFileNameCloud()
        {
            var newFiles = folderConfigDisk.FolderDisk.Where(p => !folderConfigDesktop.FolderDesktop.Any(d => d.Name == p.Name)).ToList();

            foreach (var file in newFiles)
            {
                await DownloadFilefromCloud(file.Id, file.SHA, file.FolderId, file.Path, false);
            }
        }
        static async Task compareFileNameDesk()
        {
            var newFiles = folderConfigDesktop.FolderDesktop.Where(p => !folderConfigDisk.FolderDisk.Any(d => d.Name == p.Name)).ToList();

            foreach (var file in newFiles)
            {
                Console.WriteLine($"Новый файл на компьютере: {file.Name} в папке {file.Path}");
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
        }
        static async Task compareFileShaCloud()
        {
            var changedFiles = folderConfigDisk.FolderDisk.Where(diskFile => folderConfigDesktop.FolderDesktop.Any(desktopFile => desktopFile.Name == diskFile.Name && desktopFile.SHA != diskFile.SHA)).ToList();
            foreach (var file in changedFiles)
            {
                await DownloadFilefromCloud(file.Id, file.SHA, file.FolderId, file.Path, true);
                Console.WriteLine($"файл изменился на диске, его надо обновить на пк {file.Name}");
            }
        }

    }
}
