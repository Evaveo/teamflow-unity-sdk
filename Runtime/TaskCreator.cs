using System;
using UnityEngine;

namespace TeamflowSDK
{
    /// <summary>
    /// High-level helper: create a task (with optional photo) in one call.
    /// Attach this MonoBehaviour to any GameObject in your scene or VR rig.
    /// </summary>
    public class TaskCreator : MonoBehaviour
    {
        // ── Events ───────────────────────────────────────────────────────

        public event Action<TeamflowTask>  OnTaskCreated;
        public event Action<string>        OnError;
        public event Action<float>         OnProgress; // 0..1

        // ── Public API ───────────────────────────────────────────────────

        /// <summary>
        /// Create a task and optionally attach a photo.
        /// </summary>
        /// <param name="title">Task title (required).</param>
        /// <param name="description">Task description.</param>
        /// <param name="projectId">Target project ID.</param>
        /// <param name="photoBytes">JPEG/PNG bytes — pass null to skip upload.</param>
        /// <param name="photoFilename">Filename for the attachment.</param>
        public void CreateTask(
            string title,
            string description,
            string projectId,
            byte[] photoBytes    = null,
            string photoFilename = null)
        {
            if (!TeamflowClient.Instance.IsAuthenticated)
            {
                Raise("Not authenticated. Call TeamflowClient.Instance.Login() first.");
                return;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                Raise("Task title is required.");
                return;
            }

            if (string.IsNullOrWhiteSpace(projectId))
            {
                Raise("Project ID is required.");
                return;
            }

            OnProgress?.Invoke(0.1f);

            var payload = new CreateTaskRequest
            {
                title       = title.Trim(),
                description = description?.Trim() ?? "",
                projectId   = projectId,
                type        = "FEATURE",
                priority    = "MEDIUM",
                status      = "TODO",
            };

            TeamflowClient.Instance.CreateTask(payload,
                onSuccess: task =>
                {
                    OnProgress?.Invoke(0.5f);

                    if (photoBytes != null && photoBytes.Length > 0)
                    {
                        var filename = string.IsNullOrEmpty(photoFilename)
                            ? $"capture_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg"
                            : photoFilename;

                        TeamflowClient.Instance.UploadTaskPhoto(task.id, photoBytes, filename,
                            onSuccess: _ =>
                            {
                                OnProgress?.Invoke(1f);
                                OnTaskCreated?.Invoke(task);
                            },
                            onError: err =>
                            {
                                // Task was created — just log the upload failure
                                Debug.LogWarning($"[TeamflowSDK] Photo upload failed: {err}");
                                OnProgress?.Invoke(1f);
                                OnTaskCreated?.Invoke(task); // still success
                            });
                    }
                    else
                    {
                        OnProgress?.Invoke(1f);
                        OnTaskCreated?.Invoke(task);
                    }
                },
                onError: err =>
                {
                    OnProgress?.Invoke(0f);
                    Raise(err);
                });
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private void Raise(string msg)
        {
            Debug.LogWarning($"[TeamflowSDK] TaskCreator error: {msg}");
            OnError?.Invoke(msg);
        }
    }
}
