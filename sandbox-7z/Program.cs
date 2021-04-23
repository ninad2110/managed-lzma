using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ManagedLzma.LZMA.Master.SevenZip;
using ManagedLzma.SevenZip;
using ManagedLzma.SevenZip.FileModel;

namespace sandbox_7z
{
    static class Program
    {
        class Password : master._7zip.Legacy.IPasswordProvider
        {
            string _pw;

            public Password(string pw)
            {
                _pw = pw;
            }

            string master._7zip.Legacy.IPasswordProvider.CryptoGetTextPassword()
            {
                return _pw;
            }
        }

        [STAThread]
        static void Main()
        {
            //ninad2110 : Set to true for packing an archieve, false for unpacking
            bool pack = false;

            if (pack)
            {
                CreateArchieve(@"..\..\Example\Sample", @"..\..\Example\Pack", "trial.7z");
                //CreateArchieve(@"D:\PRJ_1N8ZQJ26GXIE27DVJ2JCDH5LW", @"..\..\Example\Pack", "my.7z");
            }
            else
            {
                UnpackArchive(@"..\..\Example\Pack\trial.7z", @"..\..\Example\Unpack");
            
                //UnpackArchive(@"D:\MyArchive.7z", @"..\..\Example\Unpack");
            }
        }


        public static bool CreateArchieve(string sourceFolder, string destinationFolder, string outputFilename, string password = "")
        {
            Task.Run(async delegate {

                string file7z = Path.Combine(destinationFolder, outputFilename);
                using (var archiveStream = new FileStream(file7z, FileMode.Create, FileAccess.ReadWrite, FileShare.Delete))
                using (var archiveWriter = ManagedLzma.SevenZip.Writer.ArchiveWriter.Create(archiveStream, false))
                {
                    var encoder = new ManagedLzma.SevenZip.Writer.EncoderDefinition();
                    ManagedLzma.SevenZip.Writer.EncoderNodeDefinition node1 = null;
                    ManagedLzma.SevenZip.Writer.EncoderNodeDefinition node2 = null;

                    node1 = encoder.CreateEncoder(new ManagedLzma.SevenZip.Writer.Lzma2EncoderSettings(new ManagedLzma.LZMA2.EncoderSettings()));

                    if (!string.IsNullOrEmpty(password))
                    {
                        node2 = encoder.CreateEncoder(new ManagedLzma.SevenZip.Writer.AesEncoderSettings(ManagedLzma.PasswordStorage.Create(password)));
                    }

                    if (node1 != null && node2 != null)
                    {
                        encoder.Connect(encoder.GetContentSource(), node1.GetInput(0));
                        encoder.Connect(node1.GetOutput(0), node2.GetInput(0));
                        encoder.Connect(node2.GetOutput(0), encoder.CreateStorageSink());
                    }
                    else
                    {
                        encoder.Connect(encoder.GetContentSource(), (node1 ?? node2).GetInput(0));
                        encoder.Connect((node1 ?? node2).GetOutput(0), encoder.CreateStorageSink());
                    }
                    encoder.Complete();

                    var metadata = new ManagedLzma.SevenZip.Writer.ArchiveMetadataRecorder();

                    var directory = new DirectoryInfo(sourceFolder);

                    bool useDistinctEncoders = false;

                    if (useDistinctEncoders)
                    {
                        foreach (var file in directory.EnumerateFiles())
                        {
                            using (var session = archiveWriter.BeginEncoding(encoder, true))
                            {
                                using (var fileStream = file.OpenRead())
                                {
                                    var result = await session.AppendStream(fileStream, true);
                                    metadata.AppendFile(file.Name, result.Length, result.Checksum, file.Attributes, file.CreationTimeUtc, file.LastWriteTimeUtc, file.LastAccessTimeUtc);
                                }

                                // TODO: ensure that everything still aborts properly if we don't call complete
                                await session.Complete();
                            }
                        }
                    }
                    else
                    {
                        using (var session = archiveWriter.BeginEncoding(encoder, true))
                        {
                            int offset = Path.GetFullPath(directory.Parent.FullName).Length + 1; 
                            foreach (var file in directory.EnumerateFiles("*",SearchOption.AllDirectories))
                            {
                                using (var fileStream = file.OpenRead())
                                {
                                    var result = await session.AppendStream(fileStream, true);
                                    string relativePath = Path.GetFullPath(file.FullName).Substring(offset);
                                    metadata.AppendFile(relativePath, result.Length, result.Checksum, file.Attributes, file.CreationTimeUtc, file.LastWriteTimeUtc, file.LastAccessTimeUtc);
                                }
                            }

                            // TODO: ensure that everything still aborts properly if we don't call complete
                            await session.Complete();
                        }
                    }

                    await archiveWriter.WriteMetadata(metadata);
                    await archiveWriter.WriteHeader();
                }
            }).GetAwaiter().GetResult();

            return true; 
        }

        private static void UnpackArchive(string archiveFileName, string targetDirectory, string password = null)
        {
            UnpackArchive(archiveFileName, targetDirectory, password != null ? ManagedLzma.PasswordStorage.Create(password) : null);
        }

        private static void UnpackArchive(string archiveFileName, string targetDirectory, ManagedLzma.PasswordStorage password)
        {
            if (!File.Exists(archiveFileName))
                throw new FileNotFoundException("Archive not found.", archiveFileName);

            // Ensure that the target directory exists.
            Directory.CreateDirectory(targetDirectory);

            using (var archiveStream = new FileStream(archiveFileName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var archiveMetadataReader = new ManagedLzma.SevenZip.FileModel.ArchiveFileModelMetadataReader();
                var archiveFileModel = archiveMetadataReader.ReadMetadata(archiveStream, password);
                var archiveMetadata = archiveFileModel.Metadata;

                for (int sectionIndex = 0; sectionIndex < archiveMetadata.DecoderSections.Length; sectionIndex++)
                {
                    var sectionReader = new ManagedLzma.SevenZip.Reader.DecodedSectionReader(archiveStream, archiveMetadata, sectionIndex, password);
                    var sectionFiles = archiveFileModel.GetFilesInSection(sectionIndex);

                    // The section reader is constructed from metadata, if the counts do not match there must be a bug somewhere.
                    System.Diagnostics.Debug.Assert(sectionFiles.Count == sectionReader.StreamCount);

                    // The section reader iterates over all files in the section. NextStream advances the iterator.
                    for (; sectionReader.CurrentStreamIndex < sectionReader.StreamCount; sectionReader.NextStream())
                    {
                        var fileMetadata = sectionFiles[sectionReader.CurrentStreamIndex];

                        // The ArchiveFileModelMetadataReader we used above processes special marker nodes and resolves some conflicts
                        // in the archive metadata so we don't have to deal with them. In these cases there will be no file metadata
                        // produced and we should skip the stream. If you want to process these cases manually you should use a different
                        // MetadataReader subclass or write your own subclass.
                        if (fileMetadata == null)
                            continue;

                        // These asserts need to hold, otherwise there's a bug in the mapping the metadata reader produced.
                        System.Diagnostics.Debug.Assert(fileMetadata.Stream.SectionIndex == sectionIndex);
                        System.Diagnostics.Debug.Assert(fileMetadata.Stream.StreamIndex == sectionReader.CurrentStreamIndex);

                        // Ensure that the target directory is created.
                        var filename = Path.Combine(targetDirectory, fileMetadata.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(filename));

                        // NOTE: you can have two using-statements here if you want to be explicit about it, but disposing the
                        //       stream provided by the section reader is not mandatory, it is owned by the the section reader
                        //       and will be auto-closed when moving to the next stream or when disposing the section reader.
                        using (var stream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.Delete))
                            sectionReader.OpenStream().CopyTo(stream);

                        SetFileAttributes(filename, fileMetadata);
                    }
                }

                // Create empty files and empty directories.
                UnpackArchiveStructure(archiveFileModel.RootFolder, targetDirectory);
            }
        }

        private static void UnpackArchiveStructure(ManagedLzma.SevenZip.FileModel.ArchivedFolder folder, string targetDirectory)
        {
            if (folder.Items.IsEmpty)
            {
                // Empty folders need to be created manually since the unpacking code doesn't try to write into it.
                Directory.CreateDirectory(targetDirectory);
            }
            else
            {
                foreach (var item in folder.Items)
                {
                    var file = item as ManagedLzma.SevenZip.FileModel.ArchivedFile;
                    if (file != null)
                    {
                        // Files without content are not iterated during normal unpacking so we need to create them manually.
                        if (file.Stream.IsUndefined)
                        {
                            System.Diagnostics.Debug.Assert(file.Length == 0); // If the file has no content then its length should be zero, otherwise something is wrong.

                            var filename = Path.Combine(targetDirectory, file.Name);
                            using (var stream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.Delete))
                            {
                                // Nothing to do, FileMode.Create already truncates the file on opening.
                            }

                            SetFileAttributes(filename, file);
                        }
                    }

                    var subfolder = item as ManagedLzma.SevenZip.FileModel.ArchivedFolder;
                    if (subfolder != null)
                        UnpackArchiveStructure(subfolder, Path.Combine(targetDirectory, subfolder.Name));
                }
            }
        }

        private static void SetFileAttributes(string path, ManagedLzma.SevenZip.FileModel.ArchivedFile file)
        {
            if (file.Attributes.HasValue)
            {
                // When calling File.SetAttributes we need to preserve existing attributes which are not part of the archive

                var attr = File.GetAttributes(path);
                const FileAttributes kAttrMask = ArchivedAttributesExtensions.FileAttributeMask;
                attr = (attr & ~kAttrMask) | (file.Attributes.Value.ToFileAttributes() & kAttrMask);
                File.SetAttributes(path, attr);
            }
        }
    }
}
