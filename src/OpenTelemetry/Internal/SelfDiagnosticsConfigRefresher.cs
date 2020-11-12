// <copyright file="SelfDiagnosticsConfigRefresher.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

namespace OpenTelemetry.Internal
{
    /// <summary>
    /// SelfDiagnosticsConfigRefresher class checks a location for a configuration file
    /// and open a MemoryMappedFile of a configured size at the configured file path.
    /// The class provides a stream object with proper write position if the configuration
    /// file is present and valid. Otherwise, the stream object would be unavailable,
    /// nothing will be logged to any file.
    /// </summary>
    internal class SelfDiagnosticsConfigRefresher : IDisposable
    {
        private const int ConfigurationUpdatePeriodMilliSeconds = 3000;

        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly Task worker;
        private readonly SelfDiagnosticsConfigParser configParser;

        /// <summary>
        /// t_memoryMappedFileCache is a handle kept in thread-local storage as a cache to indicate whether the cached
        /// t_viewStream is created from the current m_memoryMappedFile.
        /// </summary>
        private readonly ThreadLocal<MemoryMappedFile> memoryMappedFileCache = new ThreadLocal<MemoryMappedFile>(true);
        private readonly ThreadLocal<MemoryMappedViewStream> viewStream = new ThreadLocal<MemoryMappedViewStream>(true);
        private bool disposedValue;

        // Once the configuration file is valid, an eventListener object will be created.
        // Commented out for now to avoid the "field was never used" compiler error.
        private SelfDiagnosticsEventListener eventListener;
        private volatile MemoryMappedFile memoryMappedFile;
        private string logDirectory;  // Log directory for log files
        private int logFileSize;  // Log file size in bytes
        private long logFilePosition;  // The logger will write into the byte at this position
        private EventLevel logEventLevel;

        public SelfDiagnosticsConfigRefresher()
        {
            this.configParser = new SelfDiagnosticsConfigParser();
            this.UpdateMemoryMappedFileFromConfiguration();
            this.cancellationTokenSource = new CancellationTokenSource();
            this.worker = Task.Run(() => this.Worker(this.cancellationTokenSource.Token), this.cancellationTokenSource.Token);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Try to get the log stream which is seeked to the position where the next line of log should be written.
        /// </summary>
        /// <param name="byteCount">The number of bytes that need to be written.</param>
        /// <param name="stream">When this method returns, contains the Stream object where `byteCount` of bytes can be written.</param>
        /// <param name="availableByteCount">The number of bytes that is remaining until the end of the stream.</param>
        /// <returns>Whether the logger should log in the stream.</returns>
        public virtual bool TryGetLogStream(int byteCount, out Stream stream, out int availableByteCount)
        {
            if (this.memoryMappedFile == null)
            {
                stream = null;
                availableByteCount = 0;
                return false;
            }

            try
            {
                var cachedViewStream = this.viewStream.Value;

                // Each thread has its own MemoryMappedViewStream created from the only one MemoryMappedFile.
                // Once worker thread updates the MemoryMappedFile, all the cached ViewStream objects become
                // obsolete.
                // Each thread creates a new MemoryMappedViewStream the next time it tries to retrieve it.
                // Whether the MemoryMappedViewStream is obsolete is determined by comparing the current
                // MemoryMappedFile object with the MemoryMappedFile object cached at the creation time of the
                // MemoryMappedViewStream.
                if (cachedViewStream == null || this.memoryMappedFileCache.Value != this.memoryMappedFile)
                {
                    // Race condition: The code might reach here right after the worker thread sets memoryMappedFile
                    // to null in CloseLogFile().
                    // In this case, let the NullReferenceException be caught and fail silently.
                    // By design, all events captured will be dropped during a configuration file refresh if
                    // the file changed, regardless whether the file is deleted or updated.
                    cachedViewStream = this.memoryMappedFile.CreateViewStream();
                    this.viewStream.Value = cachedViewStream;
                    this.memoryMappedFileCache.Value = this.memoryMappedFile;
                }

                long beginPosition, endPosition;
                do
                {
                    beginPosition = this.logFilePosition;
                    endPosition = beginPosition + byteCount;
                    if (endPosition >= this.logFileSize)
                    {
                        endPosition %= this.logFileSize;
                    }
                }
                while (beginPosition != Interlocked.CompareExchange(ref this.logFilePosition, endPosition, beginPosition));
                availableByteCount = (int)(this.logFileSize - beginPosition);
                cachedViewStream.Seek(beginPosition, SeekOrigin.Begin);
                stream = cachedViewStream;
                return true;
            }
            catch (Exception)
            {
                stream = null;
                availableByteCount = 0;
                return false;
            }
        }

        private async Task Worker(CancellationToken cancellationToken)
        {
            await Task.Delay(ConfigurationUpdatePeriodMilliSeconds, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                this.UpdateMemoryMappedFileFromConfiguration();
                await Task.Delay(ConfigurationUpdatePeriodMilliSeconds, cancellationToken).ConfigureAwait(false);
            }
        }

        private void UpdateMemoryMappedFileFromConfiguration()
        {
            if (this.configParser.TryGetConfiguration(out string newLogDirectory, out int fileSizeInKB, out EventLevel newEventLevel))
            {
                int newFileSize = fileSizeInKB * 1024;
                if (!newLogDirectory.Equals(this.logDirectory) || this.logFileSize != newFileSize)
                {
                    this.CloseLogFile();
                    this.OpenLogFile(newLogDirectory, newFileSize);
                }

                if (!newEventLevel.Equals(this.logEventLevel))
                {
                    if (this.eventListener != null)
                    {
                        this.eventListener.Dispose();
                    }

                    this.eventListener = new SelfDiagnosticsEventListener(newEventLevel, this);
                    this.logEventLevel = newEventLevel;
                }
            }
            else
            {
                this.CloseLogFile();
            }
        }

        private void CloseLogFile()
        {
            MemoryMappedFile mmf = Interlocked.CompareExchange(ref this.memoryMappedFile, null, this.memoryMappedFile);
            if (mmf != null)
            {
                // Each thread has its own MemoryMappedViewStream created from the only one MemoryMappedFile.
                // Once worker thread closes the MemoryMappedFile, all the ViewStream objects should be disposed
                // properly.
                foreach (MemoryMappedViewStream stream in this.viewStream.Values)
                {
                    if (stream != null)
                    {
                        stream.Dispose();
                    }
                }

                mmf.Dispose();
            }
        }

        private void OpenLogFile(string newLogDirectory, int newFileSize)
        {
            try
            {
                Directory.CreateDirectory(newLogDirectory);
                var fileName = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName) + "."
                    + Process.GetCurrentProcess().Id + ".log";
                var filePath = Path.Combine(newLogDirectory, fileName);
                this.memoryMappedFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Create, null, newFileSize);
                this.logDirectory = newLogDirectory;
                this.logFileSize = newFileSize;
                this.logFilePosition = 0;
            }
            catch (Exception ex)
            {
                OpenTelemetrySdkEventSource.Log.SelfDiagnosticsFileCreateException(newLogDirectory, ex);
            }
        }

        private void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    this.cancellationTokenSource.Cancel(false);
                    try
                    {
                        this.worker.Wait();
                    }
                    catch (AggregateException)
                    {
                    }
                    finally
                    {
                        this.cancellationTokenSource.Dispose();
                    }

                    // Ensure worker thread properly finishes.
                    // Or it might have created another MemoryMappedFile in that thread
                    // after the CloseLogFile() below is called.
                    this.CloseLogFile();
                }

                this.disposedValue = true;
            }
        }
    }
}