namespace ThatsMyQuant.Contracts.Models;

public enum Role { Admin, Operator, ReadOnly }

public class OperatorUser
{
    public Guid      Id                 { get; set; } = Guid.NewGuid();
    public string    Username           { get; set; } = "";
    public string    Email              { get; set; } = "";
    public string    PasswordHash       { get; set; } = "";
    public Role      Role               { get; set; }
    public bool      IsActive           { get; set; } = true;
    public DateTime  CreatedAt          { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt        { get; set; }
    public bool      MustChangePassword { get; set; }
}
