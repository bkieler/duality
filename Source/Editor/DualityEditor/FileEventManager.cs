﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using System.Drawing;
using System.Xml.Linq;
using System.Text.RegularExpressions;

using Duality;
using Duality.IO;
using Duality.Resources;
using Duality.Serialization;
using Duality.Editor.Forms;
using Duality.Editor.AssetManagement;

using WeifenLuo.WinFormsUI.Docking;

namespace Duality.Editor
{
	public static class FileEventManager
	{
		private struct FileEvent : IEquatable<FileEvent>
		{
			public string Path;
			public string OldPath;
			public bool IsDirectory;
			public WatcherChangeTypes Type;

			public bool IsEmpty
			{
				get { return this.Path == null; }
			}

			public bool Equals(FileEvent other)
			{
				return
					this.Path == other.Path &&
					this.OldPath == other.OldPath &&
					this.IsDirectory == other.IsDirectory &&
					this.Type == other.Type;
			}
			public override bool Equals(object obj)
			{
				if (obj is FileEvent)
					return this.Equals((FileEvent)obj);
				else
					return false;
			}
			public override int GetHashCode()
			{
				int hash = 17;
				MathF.CombineHashCode(ref hash, this.Path != null ? this.Path.GetHashCode() : 0);
				MathF.CombineHashCode(ref hash, this.OldPath != null ? this.OldPath.GetHashCode() : 0);
				MathF.CombineHashCode(ref hash, this.IsDirectory ? 23 : 0);
				MathF.CombineHashCode(ref hash, (int)this.Type);
				return hash;
			}
			public override string ToString()
			{
				return string.Format("{0} '{1}'", this.Type, this.Path);
			}
		}

		private static DateTime          lastEventProc           = DateTime.Now;
		private static FileSystemWatcher pluginWatcherWorking    = null;
		private static FileSystemWatcher pluginWatcherExec       = null;
		private static FileSystemWatcher dataDirWatcherFile      = null;
		private static FileSystemWatcher dataDirWatcherDirectory = null;
		private static FileSystemWatcher sourceDirWatcher        = null;
		private static HashSet<string>   reimportSchedule        = new HashSet<string>();
		private static HashSet<string>   editorModifiedFiles     = new HashSet<string>();
		private static HashSet<string>   editorModifiedFilesLast = new HashSet<string>();
		private static List<FileEvent>   dataDirEventBuffer      = new List<FileEvent>();
		private static List<FileEvent>   sourceDirEventBuffer    = new List<FileEvent>();


		public static event EventHandler<ResourceEventArgs>          ResourceCreated   = null;
		public static event EventHandler<ResourceEventArgs>          ResourceDeleted   = null;
		public static event EventHandler<ResourceEventArgs>          ResourceModified  = null;
		public static event EventHandler<ResourceRenamedEventArgs>   ResourceRenamed   = null;
		public static event EventHandler<FileSystemEventArgs>        PluginChanged     = null;
		public static event EventHandler<BeginGlobalRenameEventArgs> BeginGlobalRename = null;
		
		
		internal static void Init()
		{
			// Set up different file system watchers
			pluginWatcherWorking = new FileSystemWatcher();
			pluginWatcherWorking.SynchronizingObject = DualityEditorApp.MainForm;
			pluginWatcherWorking.EnableRaisingEvents = false;
			pluginWatcherWorking.Filter = "*.dll";
			pluginWatcherWorking.IncludeSubdirectories = true;
			pluginWatcherWorking.NotifyFilter = NotifyFilters.LastWrite;
			pluginWatcherWorking.Path = DualityApp.PluginDirectory;
			pluginWatcherWorking.Changed += corePluginWatcher_Changed;
			pluginWatcherWorking.Created += corePluginWatcher_Changed;
			pluginWatcherWorking.EnableRaisingEvents = true;

			string execPluginDir = Path.Combine(PathHelper.ExecutingAssemblyDir, DualityApp.PluginDirectory);
			if (Path.GetFullPath(execPluginDir).ToLower() != Path.GetFullPath(DualityApp.PluginDirectory).ToLower() && Directory.Exists(execPluginDir))
			{
				pluginWatcherExec = new FileSystemWatcher();
				pluginWatcherExec.SynchronizingObject = DualityEditorApp.MainForm;
				pluginWatcherExec.EnableRaisingEvents = false;
				pluginWatcherExec.Filter = "*.dll";
				pluginWatcherExec.IncludeSubdirectories = true;
				pluginWatcherExec.NotifyFilter = NotifyFilters.LastWrite;
				pluginWatcherExec.Path = execPluginDir;
				pluginWatcherExec.Changed += corePluginWatcher_Changed;
				pluginWatcherExec.Created += corePluginWatcher_Changed;
				pluginWatcherExec.EnableRaisingEvents = true;
			}
			
			dataDirWatcherFile = new FileSystemWatcher();
			dataDirWatcherFile.SynchronizingObject = DualityEditorApp.MainForm;
			dataDirWatcherFile.EnableRaisingEvents = false;
			dataDirWatcherFile.IncludeSubdirectories = true;
			dataDirWatcherFile.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
			dataDirWatcherFile.Path = DualityApp.DataDirectory;
			dataDirWatcherFile.Created += fileSystemWatcher_ForwardData;
			dataDirWatcherFile.Changed += fileSystemWatcher_ForwardData;
			dataDirWatcherFile.Deleted += fileSystemWatcher_ForwardData;
			dataDirWatcherFile.Renamed += fileSystemWatcher_ForwardData;
			dataDirWatcherFile.EnableRaisingEvents = true;

			dataDirWatcherDirectory = new FileSystemWatcher();
			dataDirWatcherDirectory.SynchronizingObject = DualityEditorApp.MainForm;
			dataDirWatcherDirectory.EnableRaisingEvents = false;
			dataDirWatcherDirectory.IncludeSubdirectories = true;
			dataDirWatcherDirectory.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
			dataDirWatcherDirectory.Path = DualityApp.DataDirectory;
			dataDirWatcherDirectory.Created += fileSystemWatcher_ForwardData;
			dataDirWatcherDirectory.Changed += fileSystemWatcher_ForwardData;
			dataDirWatcherDirectory.Deleted += fileSystemWatcher_ForwardData;
			dataDirWatcherDirectory.Renamed += fileSystemWatcher_ForwardData;
			dataDirWatcherDirectory.EnableRaisingEvents = true;
			
			sourceDirWatcher = new FileSystemWatcher();
			sourceDirWatcher.SynchronizingObject = DualityEditorApp.MainForm;
			sourceDirWatcher.EnableRaisingEvents = false;
			sourceDirWatcher.IncludeSubdirectories = true;
			sourceDirWatcher.Path = EditorHelper.SourceDirectory;
			sourceDirWatcher.Created += fileSystemWatcher_ForwardSource;
			sourceDirWatcher.Changed += fileSystemWatcher_ForwardSource;
			sourceDirWatcher.Deleted += fileSystemWatcher_ForwardSource;
			sourceDirWatcher.Renamed += fileSystemWatcher_ForwardSource;
			sourceDirWatcher.EnableRaisingEvents = true;

			// Register events
			DualityEditorApp.MainForm.Activated += mainForm_Activated;
			DualityEditorApp.EditorIdling += DualityEditorApp_EditorIdling;
			Resource.ResourceSaved += Resource_ResourceSaved;
		}
		internal static void Terminate()
		{
			// Unregister events
			DualityEditorApp.MainForm.Activated -= mainForm_Activated;
			DualityEditorApp.EditorIdling -= DualityEditorApp_EditorIdling;
			Resource.ResourceSaved -= Resource_ResourceSaved;

			// Destroy file system watchers
			pluginWatcherWorking.EnableRaisingEvents = false;
			pluginWatcherWorking.Changed -= corePluginWatcher_Changed;
			pluginWatcherWorking.Created -= corePluginWatcher_Changed;
			pluginWatcherWorking.SynchronizingObject = null;
			pluginWatcherWorking.Dispose();
			pluginWatcherWorking = null;

			dataDirWatcherFile.EnableRaisingEvents = false;
			dataDirWatcherFile.Created -= fileSystemWatcher_ForwardData;
			dataDirWatcherFile.Changed -= fileSystemWatcher_ForwardData;
			dataDirWatcherFile.Deleted -= fileSystemWatcher_ForwardData;
			dataDirWatcherFile.Renamed -= fileSystemWatcher_ForwardData;
			dataDirWatcherFile.SynchronizingObject = null;
			dataDirWatcherFile.Dispose();
			dataDirWatcherFile = null;

			dataDirWatcherDirectory.EnableRaisingEvents = false;
			dataDirWatcherDirectory.Created -= fileSystemWatcher_ForwardData;
			dataDirWatcherDirectory.Changed -= fileSystemWatcher_ForwardData;
			dataDirWatcherDirectory.Deleted -= fileSystemWatcher_ForwardData;
			dataDirWatcherDirectory.Renamed -= fileSystemWatcher_ForwardData;
			dataDirWatcherDirectory.SynchronizingObject = null;
			dataDirWatcherDirectory.Dispose();
			dataDirWatcherDirectory = null;

			sourceDirWatcher.EnableRaisingEvents = false;
			sourceDirWatcher.Created -= fileSystemWatcher_ForwardSource;
			sourceDirWatcher.Changed -= fileSystemWatcher_ForwardSource;
			sourceDirWatcher.Deleted -= fileSystemWatcher_ForwardSource;
			sourceDirWatcher.Renamed -= fileSystemWatcher_ForwardSource;
			sourceDirWatcher.SynchronizingObject = null;
			sourceDirWatcher.Dispose();
			sourceDirWatcher = null;
		}


		private static void AggregateFileSystemEvents(List<FileEvent> events)
		{
			// Traverse events and aggregate with previous events, so the latest event
			// in an aggregate chain is the one that defines event order.
			for (int currentIndex = events.Count - 1; currentIndex > 0; currentIndex--)
			{
				FileEvent current = events[currentIndex];
				string currentOldFileName = Path.GetFileName(current.OldPath);
				string currentFileName = Path.GetFileName(current.Path);

				for (int prevIndex = currentIndex - 1; prevIndex >= 0; prevIndex--)
				{
					FileEvent prev = events[prevIndex];
					string prevFileName = Path.GetFileName(current.Path);

					// Aggregate sequential renames / moves of the same file
					if (current.Type == WatcherChangeTypes.Renamed &&
						prev.Type == WatcherChangeTypes.Renamed &&
						currentOldFileName == prevFileName)
					{
						current.OldPath = prev.OldPath;
						events.RemoveAt(prevIndex);
						currentIndex--;
						continue;
					}

					// Aggregate "delete A, then rename B to A" into "rename B to A, changed A" events.
					// Some applications (like Photoshop) do stuff like that when saving files.
					if (current.Type == WatcherChangeTypes.Renamed &&
						prev.Type == WatcherChangeTypes.Deleted &&
						current.Path == prev.Path)
					{
						FileEvent rename = new FileEvent();
						rename.Type = WatcherChangeTypes.Renamed;
						rename.OldPath = prev.Path;
						rename.Path = current.Path;
						rename.IsDirectory = current.IsDirectory;
						events.Insert(currentIndex, rename);
						currentIndex++;

						current.Type = WatcherChangeTypes.Changed;
						current.OldPath = current.Path;
						events.RemoveAt(prevIndex);
						currentIndex--;
						continue;
					}

					// Aggregate "delete Foo/A, create Bar/A" to "rename Foo/A to Bar/A" events.
					if (current.Type == WatcherChangeTypes.Created &&
						prev.Type == WatcherChangeTypes.Deleted &&
						currentFileName == prevFileName)
					{
						current.Type = WatcherChangeTypes.Renamed;
						current.OldPath = prev.Path;
						events.RemoveAt(prevIndex);
						currentIndex--;
						continue;
					}
				}

				// Assign back the modified current file event after its potential aggregation
				events[currentIndex] = current;
			}
		}
		private static void FilterFileSystemEvents(List<FileEvent> events)
		{
			for (int currentIndex = events.Count - 1; currentIndex > 0; currentIndex--)
			{
				FileEvent current = events[currentIndex];

				// Discard pointless rename events
				if (current.Type == WatcherChangeTypes.Renamed &&
					current.OldPath == current.Path)
				{
					events.RemoveAt(currentIndex);
					continue;
				}

				// Discard changes made by the editor itself
				if (current.Type == WatcherChangeTypes.Changed && 
					IsPathEditorModified(current.Path))
				{
					events.RemoveAt(currentIndex);
					continue;
				}
			}
		}

		private static FileEvent TranslateFileEvent(FileSystemEventArgs watcherEvent, bool isDirectory)
		{
			FileEvent fileEvent;
			fileEvent.Path = watcherEvent.FullPath;
			fileEvent.IsDirectory = isDirectory;
			fileEvent.Type = watcherEvent.ChangeType;

			if (watcherEvent is RenamedEventArgs)
				fileEvent.OldPath = ((RenamedEventArgs)watcherEvent).OldFullPath;
			else
				fileEvent.OldPath = fileEvent.Path;

			return fileEvent;
		}

		private static void PushDataDirEvent(FileSystemEventArgs e, bool isDirectory)
		{
			if (!PathHelper.IsPathVisible(e.FullPath)) return;

			// Translate the file system watcher event into out local event struct
			FileEvent fileEvent = TranslateFileEvent(e, isDirectory);

			// Aggregate all events of the same type for the same path into one event
			dataDirEventBuffer.RemoveAll(f => f.Path == fileEvent.Path && f.Type == fileEvent.Type);
			dataDirEventBuffer.Add(fileEvent);
		}
		private static void ProcessDataDirEvents()
		{
			// Retrieve and pre-process events, so we end up with only the relevant events.
			AggregateFileSystemEvents(dataDirEventBuffer);
			FilterFileSystemEvents(dataDirEventBuffer);

			// System internal event processing / do all the low-level stuff
			HandleDataDirEvents(dataDirEventBuffer);

			// Fire editor-wide events to allow plugins and editor modules to react
			InvokeGlobalDataDirEventHandlers(dataDirEventBuffer);

			// Handled all events, start over with an empty buffer
			dataDirEventBuffer.Clear();
		}
		private static void HandleDataDirEvents(List<FileEvent> eventList)
		{
			// Handle each event according to its type
			List<FileEvent> renameEventBuffer = null;
			HashSet<string> sourceMediaDeleteSchedule = null;
			for (int i = 0; i < eventList.Count; i++)
			{
				FileEvent fileEvent = eventList[i];

				if (fileEvent.Type == WatcherChangeTypes.Changed)
				{
					HandleDataDirChangeEvent(fileEvent);
				}
				else if (fileEvent.Type == WatcherChangeTypes.Deleted)
				{
					HandleDataDirDeleteEvent(fileEvent, ref sourceMediaDeleteSchedule);
				}
				else if (fileEvent.Type == WatcherChangeTypes.Renamed)
				{
					HandleDataDirRenameEvent(fileEvent, ref renameEventBuffer);
				}
			}

			// If we scheduled source / media files for deletion, do it now at once
			if (sourceMediaDeleteSchedule != null)
			{
				// Gather a list of directories from which we're removing
				HashSet<string> affectedDirectories = new HashSet<string>();
				foreach (string file in sourceMediaDeleteSchedule)
				{
					affectedDirectories.Add(Path.GetDirectoryName(file));
				}

				// Send all the files to the recycle bin
				RecycleBin.SendSilent(sourceMediaDeleteSchedule);

				// Remove directories that are now empty
				foreach (string dir in affectedDirectories)
				{
					PathHelper.DeleteEmptyDirectory(dir, true);
				}
			}

			// If required, perform a global rename operation in all existing content
			if (renameEventBuffer != null)
			{
				// Don't do it now - schedule it for the main form event loop so we don't block here.
				DualityEditorApp.MainForm.BeginInvoke((Action)delegate () {
					ProcessingBigTaskDialog taskDialog = new ProcessingBigTaskDialog(
						Properties.GeneralRes.TaskRenameContentRefs_Caption,
						Properties.GeneralRes.TaskRenameContentRefs_Desc,
						async_RenameContentRefs, renameEventBuffer);
					taskDialog.ShowDialog(DualityEditorApp.MainForm);
				});
			}
		}
		private static void HandleDataDirChangeEvent(FileEvent fileEvent)
		{
			// Unregister outdated resources when modified outside the editor
			if (Resource.IsResourceFile(fileEvent.Path) && ContentProvider.HasContent(fileEvent.Path))
			{
				ContentRef<Resource> resRef = new ContentRef<Resource>(null, fileEvent.Path);
				bool isCurrentScene = resRef.Is<Scene>() && Scene.Current == resRef.Res;
				if (isCurrentScene || DualityEditorApp.IsResourceUnsaved(fileEvent.Path))
				{
					DialogResult result = MessageBox.Show(
						string.Format(Properties.GeneralRes.Msg_ConfirmReloadResource_Text, fileEvent.Path),
						Properties.GeneralRes.Msg_ConfirmReloadResource_Caption,
						MessageBoxButtons.YesNo,
						MessageBoxIcon.Exclamation);
					if (result == DialogResult.Yes)
					{
						string curScenePath = Scene.CurrentPath;
						ContentProvider.RemoveContent(fileEvent.Path);
						if (isCurrentScene) Scene.SwitchTo(ContentProvider.RequestContent<Scene>(curScenePath), true);
					}
				}
				else
				{
					ContentProvider.RemoveContent(fileEvent.Path);
				}
			}
		}
		private static void HandleDataDirDeleteEvent(FileEvent fileEvent, ref HashSet<string> sourceMediaDeleteSchedule)
		{
			if (Resource.IsResourceFile(fileEvent.Path) || fileEvent.IsDirectory)
			{
				// Schedule Source/Media file deletion to keep it organized / synced with Resource Data
				if (sourceMediaDeleteSchedule == null)
					sourceMediaDeleteSchedule = new HashSet<string>();
				GetDeleteSourceMediaFilePaths(fileEvent, sourceMediaDeleteSchedule);

				// Unregister no-longer existing resources
				if (fileEvent.IsDirectory)
					ContentProvider.RemoveContentTree(fileEvent.Path);
				else
					ContentProvider.RemoveContent(fileEvent.Path);
			}
		}
		private static void HandleDataDirRenameEvent(FileEvent fileEvent, ref List<FileEvent> renameEventBuffer)
		{
			if (Resource.IsResourceFile(fileEvent.Path) || fileEvent.IsDirectory)
			{
				// Determine which Source / Media files would belong to this Resource - before moving it
				string[] oldMediaPaths = PreMoveSourceMediaFile(fileEvent);

				// Rename registered content
				if (fileEvent.IsDirectory)
					ContentProvider.RenameContentTree(fileEvent.OldPath, fileEvent.Path);
				else
					ContentProvider.RenameContent(fileEvent.OldPath, fileEvent.Path);

				// Query skipped paths
				bool isEmptyDir = fileEvent.IsDirectory && !Directory.EnumerateFileSystemEntries(fileEvent.Path).Any();
				bool isSkippedPath = isEmptyDir;
				if (!isSkippedPath && BeginGlobalRename != null)
				{
					BeginGlobalRenameEventArgs beginGlobalRenameArgs = new BeginGlobalRenameEventArgs(
						fileEvent.Path,
						fileEvent.OldPath,
						fileEvent.IsDirectory);
					BeginGlobalRename(null, beginGlobalRenameArgs);
					isSkippedPath = beginGlobalRenameArgs.Cancel;
				}

				if (!isSkippedPath)
				{
					// Buffer rename event to perform the global rename for all at once.
					if (renameEventBuffer == null)
						renameEventBuffer = new List<FileEvent>();
					renameEventBuffer.Add(fileEvent);
				}

				if (!isSkippedPath)
				{
					// Organize the Source/Media directory accordingly
					MoveSourceMediaFile(fileEvent, oldMediaPaths);
				}
			}
		}
		private static void InvokeGlobalDataDirEventHandlers(List<FileEvent> eventList)
		{
			for (int i = 0; i < eventList.Count; i++)
			{
				FileEvent fileEvent = eventList[i];

				// Skip everything that isn't either a Resource or a directory
				if (!Resource.IsResourceFile(fileEvent.Path) && !fileEvent.IsDirectory)
					continue;

				// Fire events
				if (fileEvent.Type == WatcherChangeTypes.Changed)
				{
					if (ResourceModified != null)
						ResourceModified(null, new ResourceEventArgs(fileEvent.Path, fileEvent.IsDirectory));
				}
				else if (fileEvent.Type == WatcherChangeTypes.Created)
				{
					if (ResourceCreated != null)
						ResourceCreated(null, new ResourceEventArgs(fileEvent.Path, fileEvent.IsDirectory));
				}
				else if (fileEvent.Type == WatcherChangeTypes.Deleted)
				{
					if (ResourceDeleted != null)
						ResourceDeleted(null, new ResourceEventArgs(fileEvent.Path, fileEvent.IsDirectory));
				}
				else if (fileEvent.Type == WatcherChangeTypes.Renamed)
				{
					if (ResourceRenamed != null)
						ResourceRenamed(null, new ResourceRenamedEventArgs(fileEvent.Path, fileEvent.OldPath, fileEvent.IsDirectory));
				}
			}
		}

		private static void PushSourceDirEvent(FileSystemEventArgs e)
		{
			if (!PathHelper.IsPathVisible(e.FullPath)) return;

			// Translate the file system watcher event into out local event struct
			FileEvent fileEvent = TranslateFileEvent(e, Directory.Exists(e.FullPath));

			// Aggregate all events of the same type for the same path into one event
			sourceDirEventBuffer.RemoveAll(f => f.Path == fileEvent.Path && f.Type == fileEvent.Type);
			sourceDirEventBuffer.Add(fileEvent);
		}
		private static void ProcessSourceDirEvents()
		{
			// Retrieve and pre-process events, so we end up with only the relevant events.
			AggregateFileSystemEvents(sourceDirEventBuffer);
			FilterFileSystemEvents(sourceDirEventBuffer);

			// Process events
			for (int i = 0; i < sourceDirEventBuffer.Count; i++)
			{
				FileEvent fileEvent = sourceDirEventBuffer[i];

				// Mind modified source files for re-import
				if (fileEvent.Type == WatcherChangeTypes.Changed)
				{
					if (File.Exists(fileEvent.Path) && PathOp.IsPathLocatedIn(fileEvent.Path, EditorHelper.SourceMediaDirectory)) 
						reimportSchedule.Add(fileEvent.Path);
				}
			}

			// Handled all events, start over with an empty buffer
			dataDirEventBuffer.Clear();
		}

		public static void FlagPathEditorModified(string path)
		{
			if (string.IsNullOrEmpty(path)) return; // Ignore bad paths
			string fullPath = Path.GetFullPath(path);
			editorModifiedFiles.Add(fullPath);
			editorModifiedFilesLast.Remove(fullPath);
		}
		private static bool IsPathEditorModified(string path)
		{
			return editorModifiedFiles.Contains(Path.GetFullPath(path));
		}
		private static void GetDeleteSourceMediaFilePaths(FileEvent deleteEvent, ICollection<string> deletePathSchedule)
		{
			if (!deleteEvent.IsDirectory)
			{
				IList<string> mediaPaths = AssetManager.GetAssetSourceFiles(new ContentRef<Resource>(deleteEvent.Path));
				for (int i = 0; i < mediaPaths.Count; i++)
				{
					if (File.Exists(mediaPaths[i]))
					{
						deletePathSchedule.Add(mediaPaths[i]);
					}
				}
			}
			else
			{
				string mediaPath = Path.Combine(
					EditorHelper.SourceMediaDirectory, 
					PathHelper.MakeFilePathRelative(deleteEvent.Path, DualityApp.DataDirectory));
				if (Directory.Exists(mediaPath))
				{
					deletePathSchedule.Add(mediaPath);
				}
			}
		}
		private static string[] PreMoveSourceMediaFile(FileEvent renameEvent)
		{
			if (renameEvent.IsDirectory)
				return new string[0];
			else
				return AssetManager.GetAssetSourceFiles(new ContentRef<Resource>(renameEvent.OldPath));
		}
		private static void MoveSourceMediaFile(FileEvent renameEvent, string[] oldMediaPaths)
		{
			if (!renameEvent.IsDirectory)
			{
				string[] newMediaPaths = AssetManager.GetAssetSourceFiles(new ContentRef<Resource>(renameEvent.Path));
				for (int i = 0; i < oldMediaPaths.Length; i++)
				{
					string oldPath = oldMediaPaths[i];
					string newPath = newMediaPaths.Length > i ? newMediaPaths[i] : oldPath;

					// Move the media file to mirror the data files movement
					if (!PathOp.ArePathsEqual(oldPath, newPath))
					{
						if (File.Exists(oldPath) && !File.Exists(newPath))
						{
							Directory.CreateDirectory(Path.GetDirectoryName(newPath));
							try
							{
								File.Move(oldPath, newPath);
							}
							catch (IOException exception)
							{
								Logs.Editor.WriteWarning(
									"Unable to move source media file '{0}' to '{1}' ({2}). Copying the file instead.",
									oldPath,
									newPath,
									exception.Message);
								File.Copy(oldPath, newPath);
							}
							PathHelper.DeleteEmptyDirectory(Path.GetDirectoryName(oldPath), true);
						}
					}
				}
			}
			else
			{
				// Determine which source/media directory we're going to move
				string oldMediaPath = Path.Combine(
					EditorHelper.SourceMediaDirectory, 
					PathHelper.MakeFilePathRelative(renameEvent.OldPath, DualityApp.DataDirectory));

				// Determine where that old source/media directory needs to be moved
				string newMediaPath = Path.Combine(
					EditorHelper.SourceMediaDirectory, 
					PathHelper.MakeFilePathRelative(renameEvent.Path, DualityApp.DataDirectory));

				// Move the media directory to mirror the data directories movement
				if (!PathOp.ArePathsEqual(newMediaPath, oldMediaPath))
				{
					if (Directory.Exists(oldMediaPath) && !Directory.Exists(newMediaPath))
					{
						Directory.CreateDirectory(Path.GetDirectoryName(newMediaPath));
						try
						{
							Directory.Move(oldMediaPath, newMediaPath);
						}
						catch (IOException exception)
						{
							Logs.Editor.WriteWarning(
								"Unable to move source media directory '{0}' to '{1}' ({2}). Copying the directory instead.",
								oldMediaPath,
								newMediaPath,
								exception.Message);
							PathHelper.CopyDirectory(oldMediaPath, newMediaPath);
						}
						PathHelper.DeleteEmptyDirectory(Path.GetDirectoryName(oldMediaPath), true);
					}
				}
			}
		}

		private static void DualityEditorApp_EditorIdling(object sender, EventArgs e)
		{
			// Process file / source events regularily, if no modal dialog is open.
			if ((DateTime.Now - lastEventProc).TotalMilliseconds > 100.0d)
			{
				ProcessSourceDirEvents();
				ProcessDataDirEvents();

				// Manage the list of editor-modified files to be ignored in a 
				// two-pass process, so event order doesn't matter.
				{
					// Remove the ones that were known last time
					foreach (string file in editorModifiedFilesLast)
						editorModifiedFiles.Remove(file);

					// Mind the ones that are known right now
					editorModifiedFilesLast.Clear();
					foreach (string file in editorModifiedFiles)
						editorModifiedFilesLast.Add(file);
				}

				lastEventProc = DateTime.Now;
			}
		}
		private static void Resource_ResourceSaved(object sender, ResourceSaveEventArgs e)
		{
			FlagPathEditorModified(e.SaveAsPath);
		}
		
		private static void mainForm_Activated(object sender, EventArgs e)
		{
			// Perform scheduled source file reimports
			if (reimportSchedule.Count > 0)
			{
				// Hacky: Wait a little for the files to be accessable again (Might be used by another process)
				System.Threading.Thread.Sleep(50);

				string[] existingReImportFiles = reimportSchedule
					.Where(path => File.Exists(path))
					.ToArray();
				reimportSchedule.Clear();
				AssetManager.ReImportAssets(existingReImportFiles);
			}
		}
		private static void fileSystemWatcher_ForwardSource(object sender, FileSystemEventArgs e)
		{
			PushSourceDirEvent(e);
		}
		private static void fileSystemWatcher_ForwardData(object sender, FileSystemEventArgs e)
		{
			PushDataDirEvent(e, sender == dataDirWatcherDirectory);
		}
		private static void corePluginWatcher_Changed(object sender, FileSystemEventArgs e)
		{
			// Ignore other class libraries that clearly aren't plugins
			if (!e.FullPath.EndsWith(".core.dll", StringComparison.InvariantCultureIgnoreCase) &&
				!e.FullPath.EndsWith(".editor.dll", StringComparison.InvariantCultureIgnoreCase))
				return;

			if (PluginChanged != null)
				PluginChanged(sender, e);
		}

		private static System.Collections.IEnumerable async_RenameContentRefs(ProcessingBigTaskDialog.WorkerInterface state)
		{
			List<FileEvent> renameData = (List<FileEvent>)state.Data;
			int totalCounter = 0;
			int fileCounter = 0;
			
			// Rename in static application data
			state.StateDesc = "DualityApp Data"; yield return null;
			DualityApp.LoadAppData();
			DualityApp.LoadUserData();
			state.Progress += 0.04f; yield return null;

			totalCounter += async_RenameContentRefs_Perform(DualityApp.AppData, renameData);
			totalCounter += async_RenameContentRefs_Perform(DualityApp.UserData, renameData);
			state.Progress += 0.02f; yield return null;

			DualityApp.SaveAppData();
			DualityApp.SaveUserData();
			state.Progress += 0.04f; yield return null;

			// Special case: Current Scene in sandbox mode
			if (Sandbox.IsActive)
			{
				// Because changes we'll do will be discarded when leaving the sandbox we'll need to
				// do it the hard way - manually load an save the file.
				state.StateDesc = "Current Scene"; yield return null;
				Scene curScene = Resource.Load<Scene>(Scene.CurrentPath, null, false);
				fileCounter = async_RenameContentRefs_Perform(curScene, renameData);
				totalCounter += fileCounter;
				if (fileCounter > 0) curScene.Save(Scene.CurrentPath, false);
			}
			// Special case: Current Scene NOT in sandbox mode, but still unsaved
			else if (Scene.Current.IsRuntimeResource)
			{
				state.StateDesc = "Current Scene"; yield return null;
				fileCounter = async_RenameContentRefs_Perform(Scene.Current, renameData);
				if (fileCounter > 0)
					DualityEditorApp.NotifyObjPropChanged(null, new ObjectSelection(Scene.Current.AllObjects));
				totalCounter += fileCounter;
			}

			// Rename in actual content
			Type[] targetResTypes = renameData.Any(e => e.IsDirectory) ? null : renameData.Select(e => new ContentRef<Resource>(e.Path).ResType).ToArray();
			List<ContentRef<Resource>> loadedContent = ContentProvider.GetLoadedContent<Resource>();
			List<IContentRef> reloadContent = new List<IContentRef>();
			string[] resFiles = Resource.GetResourceFiles().ToArray();
			List<Resource> modifiedRes = new List<Resource>();
			foreach (string file in resFiles)
			{
				// Early-out, if this kind of Resource isn't able to reference the renamed Resource
				if (targetResTypes != null)
				{
					Type resType = Resource.GetTypeByFileName(file);

					if (resType == null)
					{
						Logs.Editor.WriteWarning("Could not determine Resource type for File '{0}' using file name only. Skipping it during rename.", file);
						continue;
					}

					bool canReferenceRes = false;
					foreach (Type targetType in targetResTypes)
					{
						if (ReflectionHelper.CanReferenceResource(resType, targetType))
						{
							canReferenceRes = true;
							break;
						}
					}
					if (!canReferenceRes)
					{
						state.Progress += 0.9f / resFiles.Length;
						continue;
					}
				}

				// Set displayed name
				state.StateDesc = file; yield return null;

				// Wasn't loaded before? Unload it later to keep the memory footprint small.
				bool wasLoaded = loadedContent.Any(r => r.Path == file);
				// Keep in mind that this operation is performed while Duality content was
				// in an inconsistent state. Loading Resources now may lead to wrong data.
				// Because the ContentRefs might be wrong right now.

				if (wasLoaded)
				{
					// Retrieve already loaded content
					IContentRef cr = ContentProvider.RequestContent(file);
					state.Progress += 0.45f / resFiles.Length; yield return null;

					// Perform rename and flag unsaved / modified
					fileCounter = async_RenameContentRefs_Perform(cr.Res, renameData);
					if (fileCounter > 0) modifiedRes.Add(cr.Res);
				}
				else
				{
					// Load content without initializing it
					Resource res = Resource.Load<Resource>(file, null, false);
					state.Progress += 0.45f / resFiles.Length; yield return null;

					// Perform rename and save it without making it globally available
					fileCounter = async_RenameContentRefs_Perform(res, renameData);
					if (fileCounter > 0) res.Save(null, false);
				}

				totalCounter += fileCounter;
				state.Progress += 0.45f / resFiles.Length; yield return null;
			}

			// Notify the editor about modified Resources
			if (modifiedRes.Count > 0)
			{
				DualityEditorApp.NotifyObjPropChanged(null, new ObjectSelection(modifiedRes));
			}
		}
		private static int async_RenameContentRefs_Perform(object obj, List<FileEvent> args)
		{
			int counter = 0;
			ReflectionHelper.VisitObjectsDeep<IContentRef>(obj, r => 
			{
				if (r.IsDefaultContent) return r;
				if (r.IsExplicitNull) return r;
				if (string.IsNullOrEmpty(r.Path)) return r;

				foreach (FileEvent e in args)
				{
					if (!e.IsDirectory && r.Path == e.OldPath)
					{
						r.Path = e.Path;
						counter++;
						break;
					}
					else if (e.IsDirectory && PathOp.IsPathLocatedIn(r.Path, e.OldPath))
					{
						r.Path = e.Path + r.Path.Remove(0, e.OldPath.Length);
						counter++;
						break;
					}
				}
				return r; 
			});
			return counter;
		}
	}
}
