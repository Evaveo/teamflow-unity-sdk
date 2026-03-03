using System;
using System.Collections.Generic;

namespace TeamflowSDK
{
    [Serializable]
    public class TeamflowUser
    {
        public string id;
        public string email;
        public string name;
        public string role;
        public string avatar;
    }

    [Serializable]
    public class TeamflowProject
    {
        public string id;
        public string name;
        public string description;
        public string workspaceId;
        public string status;
    }

    [Serializable]
    public class TeamflowTask
    {
        public string id;
        public string title;
        public string description;
        public string status;
        public string priority;
        public string type;
        public string projectId;
        public string createdBy;
        public string dueDate;
        public string createdAt;
    }

    [Serializable]
    public class TeamflowEpic
    {
        public string id;
        public string title;
        public string color;
        public string projectId;
    }

    [Serializable]
    public class TeamflowMember
    {
        public string user_id;
        public string name;
        public string email;
        public string avatar;
        public string role;
    }

    [Serializable]
    public class TeamflowAttachment
    {
        public string id;
        public string task_id;
        public string name;
        public string url;
        public string type;
        public string uploaded_by;
    }

    [Serializable]
    public class AttachmentsResponse
    {
        public List<TeamflowAttachment> attachments;
    }

    // ── Request DTOs ────────────────────────────────────────────────────

    [Serializable]
    public class LoginRequest
    {
        public string email;
        public string password;
    }

    [Serializable]
    public class LoginResponse
    {
        public TeamflowUser user;
        public string token;
    }

    [Serializable]
    public class CreateTaskRequest
    {
        public string title;
        public string description;
        public string projectId;
        public string type       = "FEATURE";
        public string priority   = "MEDIUM";
        public string status     = "TODO";
        public string epicId;
        public string[] assigneeIds;
    }

    [Serializable]
    public class EpicListWrapper
    {
        public List<TeamflowEpic> items;
    }

    [Serializable]
    public class MemberListWrapper
    {
        public List<TeamflowMember> items;
    }

    [Serializable]
    public class ProjectsResponse
    {
        public List<TeamflowProject> projects;
    }

    // ── Generic API error wrapper ────────────────────────────────────────

    [Serializable]
    public class ApiError
    {
        public string error;
    }
}
