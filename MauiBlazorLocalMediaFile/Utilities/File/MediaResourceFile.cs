using System.Security.Cryptography;

namespace MauiBlazorLocalMediaFile.Utilities
{
    public static class MediaResourceFile
    {
        public static async Task<string?> CreateMediaResourceFileAsync(string targetDirectoryPath, string? sourceFilePath)
        {
            if (string.IsNullOrEmpty(sourceFilePath))
            {
                return null;
            }

            using Stream stream = File.OpenRead(sourceFilePath);
            //新的文件以文件的md5为文件名，确保文件不会重复存在
            //获取文件的md5有一点耗时，暂时没想到更好的方案
            var fn = stream.CreateMD5() + Path.GetExtension(sourceFilePath);
            var targetFilePath = Path.Combine(targetDirectoryPath, fn);
            //如果文件存在就不用复制了
            if (!File.Exists(targetFilePath))
            {
                if (sourceFilePath.StartsWith(FileSystem.CacheDirectory))
                {
                    stream.Close();
                    await FileMoveAsync(sourceFilePath, targetFilePath);
                }
                else
                {
                    //将流的位置重置为起始位置
                    stream.Seek(0, SeekOrigin.Begin);
                    await FileCopyAsync(targetFilePath, stream);
                }
            }

            return MauiBlazorWebViewHandler.FilePathToUrlRelativePath(targetFilePath);
        }

        private static async Task FileCopyAsync(string targetFilePath, Stream sourceStream)
        {
            CreateFileDirectory(targetFilePath);

            using (FileStream localFileStream = File.OpenWrite(targetFilePath))
            {
                await sourceStream.CopyToAsync(localFileStream, 1024 * 1024);
            };
        }

        private static Task FileMoveAsync(string sourceFilePath, string targetFilePath)
        {
            CreateFileDirectory(targetFilePath);
            File.Move(sourceFilePath, targetFilePath);
            return Task.CompletedTask;
        }

        private static void CreateFileDirectory(string filePath)
        {
            string? directoryPath = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath!);
            }
        }

        private static string CreateMD5(this Stream stream, int bufferSize = 1024 * 1024)
        {
            using MD5 md5 = MD5.Create();
            byte[] buffer = new byte[bufferSize];
            int bytesRead;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                md5.TransformBlock(buffer, 0, bytesRead, buffer, 0);
            }

            md5.TransformFinalBlock(buffer, 0, 0);

            byte[] hash = md5.Hash ?? [];
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}
