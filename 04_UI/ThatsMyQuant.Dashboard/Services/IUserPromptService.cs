namespace ThatsMyQuant.Dashboard.Services;

public interface IUserPromptService
{
    bool Confirm(string title, string message);
}
