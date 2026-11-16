namespace AfallonCoop.World;

/// <summary>
/// Host-side player groups (MODDING_PLAN phase 4). The game itself has no group of players, only a player with
/// companion bots; a co-op group joins several players, each with their bot companions.
/// Rules (decided with the user):
///  - at most <see cref="MaxSize"/> members: players plus their adventurer bot companions (pets and summons do not count);
///  - a player's bots come into the group with them; the leader may send a bot away;
///  - the leader invites, removes and hands the lead to another player; an invite waits <see cref="InviteTimeout"/> s;
///  - a member who disconnects keeps their place (no time limit) and gets it back on reconnecting, recognised by a
///    stable key (Steam id, else name); a disconnected leader hands the lead to the next member online;
///  - a group of one member is dissolved.
/// </summary>
public sealed class GroupRegistry
{
    public const int MaxSize = 5;
    public const double InviteTimeout = 60;
    public const byte Offline = 255;

    public sealed class Member
    {
        public string Key = "";
        public string Name = "";
        public byte Id = Offline;   // session player id while online
        public int Bots;            // bot companions (kept while offline: they come back with the player)
        public bool Online => Id != Offline;
    }

    public sealed class Group
    {
        public byte Id;
        public string LeaderKey = "";
        public readonly List<Member> Members = new(); // join order = position in the group
        public Member Leader => Members.FirstOrDefault(m => m.Key == LeaderKey);
        public int Size => Members.Count + Members.Sum(m => m.Bots);
        public override string ToString() => $"#{Id} leader={LeaderKey} [{string.Join(",", Members.Select(m => m.Key + (m.Online ? "" : "(off)")))}]";
    }

    private readonly List<Group> _groups = new();
    private readonly Dictionary<byte, string> _keyOf = new();
    private readonly Dictionary<byte, string> _nameOf = new();
    private readonly Dictionary<byte, int> _bots = new();
    private readonly Dictionary<byte, (byte from, double at)> _invites = new();
    private byte _nextId = 1;

    /// <summary>The groups changed (send the new state to everyone).</summary>
    public event Action Changed;
    /// <summary>(player, message): tell one player why something did not happen, or what happened to them.</summary>
    public event Action<byte, string> Notice;
    /// <summary>(invitee, inviter): ask the invitee.</summary>
    public event Action<byte, byte> InviteSent;
    /// <summary>(owner, npc id): the leader sent this bot of the owner's away.</summary>
    public event Action<byte, int> BotDismissed;
    /// <summary>(player): a member came back to their group after a disconnect.</summary>
    public event Action<byte> Rejoined;

    public IReadOnlyList<Group> All => _groups;

    public Group GroupOf(byte player)
    {
        MemberOf(player, out var group);
        return group;
    }

    public bool IsLeader(byte player)
    {
        var member = MemberOf(player, out var group);
        return member != null && group.LeaderKey == member.Key;
    }

    public int BotsOf(byte player)
    {
        var member = MemberOf(player, out _);
        if (member != null) return member.Bots;
        return _bots.TryGetValue(player, out int bots) ? bots : 0;
    }

    /// <summary>A player is online (handshake done). Someone who left a group by disconnecting takes their place again.</summary>
    public void PlayerOnline(byte player, string key, string name)
    {
        _keyOf[player] = key;
        _nameOf[player] = name;
        var member = MemberOf(player, out _);
        if (member == null || member.Online) return;
        member.Id = player;
        member.Name = name;
        Notice?.Invoke(player, "You are back in your group.");
        Rejoined?.Invoke(player);
        Changed?.Invoke();
    }

    /// <summary>A player disconnected: their place in a group is kept for them.</summary>
    public void PlayerOffline(byte player)
    {
        DropInvitesOf(player);
        var member = MemberOf(player, out var group);
        _keyOf.Remove(player);
        _nameOf.Remove(player);
        _bots.Remove(player);
        if (member == null) return;
        member.Id = Offline;
        if (group.LeaderKey == member.Key) PassLead(group);
        Changed?.Invoke();
    }

    /// <summary>The name to show for a member (their character's, once they are in the world).</summary>
    public void SetName(byte player, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _nameOf[player] = name;
        var member = MemberOf(player, out _);
        if (member == null || member.Name == name) return;
        member.Name = name;
        Changed?.Invoke();
    }

    /// <summary>A player's number of bot companions (their machine reports it whenever it changes).</summary>
    public void SetBots(byte player, int count)
    {
        count = Math.Max(0, count);
        var member = MemberOf(player, out _);
        if (member == null)
        {
            _bots[player] = count;
            return;
        }
        if (member.Bots == count) return;
        member.Bots = count;
        Changed?.Invoke();
    }

    public void Invite(byte from, byte to, double now)
    {
        if (from == to || !_keyOf.ContainsKey(to)) return;
        var group = GroupOf(from);
        if (group != null && !IsLeader(from)) { Notice?.Invoke(from, "Only the group leader can invite."); return; }
        if (GroupOf(to) != null) { Notice?.Invoke(from, "That player is already in a group."); return; }
        if (!HasRoomFor(group, from, to)) { Notice?.Invoke(from, NoRoomMessage(to)); return; }
        if (HasPendingInvite(to, now)) { Notice?.Invoke(from, "That player is already considering an invitation."); return; }
        _invites[to] = (from, now);
        InviteSent?.Invoke(to, from);
        Notice?.Invoke(from, "Invitation sent.");
    }

    public void Answer(byte player, bool accept, double now)
    {
        if (!_invites.Remove(player, out var invite)) return;
        if (now - invite.at > InviteTimeout) { Notice?.Invoke(player, "The invitation expired."); return; }
        if (!accept) { Notice?.Invoke(invite.from, "The invitation was declined."); return; }
        if (GroupOf(player) != null || !_keyOf.ContainsKey(invite.from)) return;
        var group = GroupOf(invite.from);
        if (group != null && !IsLeader(invite.from)) { Notice?.Invoke(player, "The inviter is no longer the leader."); return; }
        if (!HasRoomFor(group, invite.from, player)) { Notice?.Invoke(player, "The group is full now."); return; }
        group ??= CreateGroupLedBy(invite.from);
        group.Members.Add(NewMember(player));
        Changed?.Invoke();
    }

    public void Leave(byte player)
    {
        _invites.Remove(player);
        var member = MemberOf(player, out var group);
        if (member == null) return;
        Remove(group, member);
        _bots[player] = member.Bots;
        Changed?.Invoke();
    }

    /// <summary>The leader removes a member (online or waiting for a reconnect), by their key.</summary>
    public void Kick(byte leader, string key)
    {
        var leaderMember = MemberOf(leader, out var group);
        if (leaderMember == null || group.LeaderKey != leaderMember.Key || key == leaderMember.Key) return;
        var member = group.Members.FirstOrDefault(m => m.Key == key);
        if (member == null) return;
        if (member.Online)
        {
            Notice?.Invoke(member.Id, "You were removed from the group.");
            _bots[member.Id] = member.Bots;
        }
        Remove(group, member);
        Changed?.Invoke();
    }

    /// <summary>The lead goes to another player of the group who is online (bots cannot lead).</summary>
    public void Promote(byte leader, byte target)
    {
        var leaderMember = MemberOf(leader, out var group);
        var targetMember = MemberOf(target, out var targetGroup);
        if (leaderMember == null || targetMember == null || group != targetGroup) return;
        if (group.LeaderKey != leaderMember.Key || !targetMember.Online) return;
        group.LeaderKey = targetMember.Key;
        Notice?.Invoke(target, "You are the group leader now.");
        Changed?.Invoke();
    }

    /// <summary>The leader sends one of a member's bot companions away (the owner's machine dismisses it).</summary>
    public void DismissBot(byte leader, byte owner, int npcId)
    {
        var group = GroupOf(leader);
        if (group == null || !IsLeader(leader) || GroupOf(owner) != group || BotsOf(owner) == 0) return;
        BotDismissed?.Invoke(owner, npcId);
    }

    private Member MemberOf(byte player, out Group group)
    {
        group = null;
        if (!_keyOf.TryGetValue(player, out var key)) return null;
        foreach (var candidate in _groups)
        {
            var member = candidate.Members.FirstOrDefault(m => m.Key == key);
            if (member == null) continue;
            group = candidate;
            return member;
        }
        return null;
    }

    private void DropInvitesOf(byte player)
    {
        _invites.Remove(player);
        var sentByPlayer = _invites.Where(invite => invite.Value.from == player).Select(invite => invite.Key).ToList();
        foreach (var invitee in sentByPlayer) _invites.Remove(invitee);
    }

    private bool HasPendingInvite(byte invitee, double now) =>
        _invites.TryGetValue(invitee, out var pending) && now - pending.at < InviteTimeout;

    private bool HasRoomFor(Group group, byte inviter, byte newcomer)
    {
        int size = group?.Size ?? 1 + BotsOf(inviter);
        return size + 1 + BotsOf(newcomer) <= MaxSize;
    }

    private string NoRoomMessage(byte newcomer)
    {
        int bots = BotsOf(newcomer);
        return bots > 0
            ? $"No room: they bring {bots} companion(s), the group would exceed {MaxSize}."
            : $"The group is full ({MaxSize}).";
    }

    private Group CreateGroupLedBy(byte leader)
    {
        var group = new Group { Id = _nextId++, LeaderKey = _keyOf[leader] };
        if (_nextId == 0) _nextId = 1;
        group.Members.Add(NewMember(leader));
        _groups.Add(group);
        return group;
    }

    private Member NewMember(byte player)
    {
        var member = new Member
        {
            Key = _keyOf[player],
            Name = _nameOf.TryGetValue(player, out var name) ? name : "",
            Id = player,
            Bots = _bots.TryGetValue(player, out var bots) ? bots : 0,
        };
        _bots.Remove(player);
        return member;
    }

    private void PassLead(Group group)
    {
        var next = group.Members.FirstOrDefault(m => m.Online && m.Key != group.LeaderKey);
        if (next == null) return;
        group.LeaderKey = next.Key;
        Notice?.Invoke(next.Id, "You are the group leader now.");
    }

    private void Remove(Group group, Member member)
    {
        group.Members.Remove(member);
        if (group.Members.Count <= 1) Disband(group);
        else if (group.LeaderKey == member.Key) ElectLeader(group);
    }

    private void Disband(Group group)
    {
        foreach (var remaining in group.Members.Where(m => m.Online))
        {
            _bots[remaining.Id] = remaining.Bots;
            Notice?.Invoke(remaining.Id, "Your group was disbanded.");
        }
        _groups.Remove(group);
    }

    private void ElectLeader(Group group)
    {
        var next = group.Members.FirstOrDefault(m => m.Online) ?? group.Members[0];
        group.LeaderKey = next.Key;
        if (next.Online) Notice?.Invoke(next.Id, "You are the group leader now.");
    }
}
