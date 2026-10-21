namespace MemberService;

public class Member
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string CardNumber { get; set; } = "";
    public bool IsBlocked { get; set; }
}
