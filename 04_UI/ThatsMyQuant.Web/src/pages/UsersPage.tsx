import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { usersApi, type OperatorUser, type UserRole, type CreateUserRequest } from "@/api/users";
import { useAuth } from "@/hooks/useAuth";
import { pushNotification } from "@/lib/notifications";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";
import { ApiError } from "@/api/client";

const ROLE_OPTIONS: UserRole[] = ["Admin", "Operator", "ReadOnly"];

const ROLE_TONE: Record<UserRole, string> = {
  Admin: "bg-rose-400/15 text-rose-400 border-rose-500/30",
  Operator: "bg-accent/15 text-accent border-accent/30",
  ReadOnly: "bg-white/5 text-muted-foreground border-white/10",
};

export function UsersPage() {
  const { hasRole, authEnabled } = useAuth();
  const isAdmin = hasRole("Admin");
  const qc = useQueryClient();

  const usersQ = useQuery({
    queryKey: ["users"],
    queryFn: usersApi.list,
    refetchInterval: 30_000,
  });

  const [showAdd, setShowAdd] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [resetId, setResetId] = useState<string | null>(null);

  const createM = useMutation({
    mutationFn: (req: CreateUserRequest) => usersApi.create(req),
    onSuccess: () => {
      pushNotification({ type: "success", title: "User created", body: "New user account is ready." });
      qc.invalidateQueries({ queryKey: ["users"] });
      setShowAdd(false);
    },
    onError: handleErr,
  });

  const removeM = useMutation({
    mutationFn: (id: string) => usersApi.remove(id),
    onSuccess: () => {
      pushNotification({ type: "info", title: "User deleted", body: "User account has been removed." });
      qc.invalidateQueries({ queryKey: ["users"] });
    },
    onError: handleErr,
  });

  if (!authEnabled) {
    return (
      <div className="space-y-4">
        <PageHeader />
        <div className="rounded-xl border border-amber-500/30 bg-amber-500/[0.04] p-6">
          <p className="text-sm font-bold mb-2 text-amber-400">Auth is disabled</p>
          <p className="text-[12px] font-serif italic text-muted-foreground/80 leading-relaxed">
            User management requires authentication to be enabled. Set{" "}
            <code className="text-accent">Auth:Enabled = true</code> in{" "}
            <code className="text-accent">appsettings.json</code> or runtime overrides, restart Listener.Api,
            then sign in as an admin.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <PageHeader />

      {!isAdmin && (
        <div className="rounded-xl border border-amber-500/30 bg-amber-500/[0.04] p-4 text-[12px] font-serif italic text-muted-foreground/80">
          You're signed in as a non-admin. The list below is read-only.
        </div>
      )}

      {/* Add user toolbar */}
      {isAdmin && (
        <div className="flex items-center justify-between gap-3 flex-wrap">
          <p className="text-[11px] font-serif italic text-muted-foreground/70">
            {usersQ.data?.length ?? 0} user{usersQ.data?.length === 1 ? "" : "s"} configured
          </p>
          <button
            onClick={() => setShowAdd((v) => !v)}
            className="px-3 py-1.5 rounded-md bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors text-[10px] font-black uppercase tracking-widest"
          >
            {showAdd ? "✕ Cancel" : "+ Add User"}
          </button>
        </div>
      )}

      {showAdd && isAdmin && (
        <AddUserForm
          onSubmit={(req) => createM.mutate(req)}
          isPending={createM.isPending}
          onCancel={() => setShowAdd(false)}
        />
      )}

      {/* Users table */}
      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
        {usersQ.isLoading ? (
          <p className="p-8 text-center text-xs italic font-serif text-muted-foreground/60">
            — loading users —
          </p>
        ) : (usersQ.data ?? []).length === 0 ? (
          <p className="p-8 text-center text-xs italic font-serif text-muted-foreground/60">
            — no users configured · seed an admin via WPF Dashboard or CLI —
          </p>
        ) : (
          <table className="w-full text-sm">
            <thead className="border-b border-white/5 text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60">
              <tr>
                <th className="text-left px-4 py-3 font-black">User</th>
                <th className="text-left px-3 py-3 font-black">Role</th>
                <th className="text-center px-3 py-3 font-black">Active</th>
                <th className="text-left px-3 py-3 font-black">Created</th>
                <th className="text-left px-3 py-3 font-black">Last Login</th>
                <th className="text-right px-4 py-3 font-black">Actions</th>
              </tr>
            </thead>
            <tbody>
              {(usersQ.data ?? []).map((u) => (
                <UserRow
                  key={u.id}
                  user={u}
                  isAdmin={isAdmin}
                  isEditing={editingId === u.id}
                  onStartEdit={() => setEditingId(u.id)}
                  onCancelEdit={() => setEditingId(null)}
                  onResetPassword={() => setResetId(u.id)}
                  onDelete={() => {
                    if (confirm(`Delete user "${u.username}"? This cannot be undone.`)) {
                      removeM.mutate(u.id);
                    }
                  }}
                />
              ))}
            </tbody>
          </table>
        )}
      </div>

      {resetId && isAdmin && (
        <ResetPasswordModal
          userId={resetId}
          username={usersQ.data?.find((u) => u.id === resetId)?.username ?? ""}
          onClose={() => setResetId(null)}
        />
      )}
    </div>
  );
}

function PageHeader() {
  return (
    <div className="relative">
      <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
      <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
        Operator Roster
      </h2>
      <p className="font-serif text-[24px] tracking-tight ml-2">
        Users
        <span className="text-muted-foreground/40 font-light text-base ml-2">
          — admin / operator / read-only roles
        </span>
      </p>
    </div>
  );
}

function AddUserForm({
  onSubmit,
  isPending,
  onCancel,
}: {
  onSubmit: (req: CreateUserRequest) => void;
  isPending: boolean;
  onCancel: () => void;
}) {
  const [username, setUsername] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState<UserRole>("Operator");

  const ok = username.trim() && email.trim() && password.length >= 6;

  return (
    <div className="rounded-xl border border-accent/30 bg-accent/[0.04] p-5 space-y-3">
      <p className="text-[10px] font-black uppercase tracking-[0.3em] text-accent/80">
        Add new user
      </p>
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
        <Field label="Username">
          <input
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="Email">
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="Password (≥ 6)">
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>
        <Field label="Role">
          <select
            value={role}
            onChange={(e) => setRole(e.target.value as UserRole)}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          >
            {ROLE_OPTIONS.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </Field>
      </div>
      <div className="flex items-center justify-end gap-2">
        <button
          onClick={onCancel}
          className="px-3 py-1.5 rounded text-[10px] font-mono uppercase tracking-widest text-muted-foreground hover:text-foreground hover:bg-white/[0.05] transition-colors"
        >
          Cancel
        </button>
        <button
          onClick={() => ok && onSubmit({ username, email, password, role })}
          disabled={!ok || isPending}
          className="px-3 py-1.5 rounded text-[10px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors disabled:opacity-40"
        >
          {isPending ? "Creating…" : "Create user"}
        </button>
      </div>
    </div>
  );
}

function UserRow({
  user: u,
  isAdmin,
  isEditing,
  onStartEdit,
  onCancelEdit,
  onResetPassword,
  onDelete,
}: {
  user: OperatorUser;
  isAdmin: boolean;
  isEditing: boolean;
  onStartEdit: () => void;
  onCancelEdit: () => void;
  onResetPassword: () => void;
  onDelete: () => void;
}) {
  const qc = useQueryClient();
  const [draftRole, setDraftRole] = useState<UserRole>(u.role);
  const [draftActive, setDraftActive] = useState(u.isActive);

  const saveM = useMutation({
    mutationFn: () => usersApi.update(u.id, { role: draftRole, isActive: draftActive }),
    onSuccess: () => {
      pushNotification({ type: "success", title: `Updated ${u.username}`, body: "User profile saved." });
      qc.invalidateQueries({ queryKey: ["users"] });
      onCancelEdit();
    },
    onError: handleErr,
  });

  return (
    <tr className="border-b border-white/[0.04] hover:bg-white/[0.02]">
      <td className="px-4 py-2.5">
        <p className="font-bold text-[12px]">{u.username}</p>
        <p className="text-[10px] font-mono text-muted-foreground/60">{u.email}</p>
      </td>
      <td className="px-3 py-2.5">
        {isEditing && isAdmin ? (
          <select
            value={draftRole}
            onChange={(e) => setDraftRole(e.target.value as UserRole)}
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[11px] font-mono"
          >
            {ROLE_OPTIONS.map((r) => (
              <option key={r} value={r}>{r}</option>
            ))}
          </select>
        ) : (
          <span className={cn(
            "px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest border",
            ROLE_TONE[u.role]
          )}>
            {u.role}
          </span>
        )}
      </td>
      <td className="px-3 py-2.5 text-center">
        {isEditing && isAdmin ? (
          <button
            role="switch"
            aria-checked={draftActive}
            onClick={() => setDraftActive((v) => !v)}
            className={cn(
              "relative w-9 h-5 rounded-full transition-colors",
              draftActive ? "bg-emerald-500/40" : "bg-rose-500/30"
            )}
          >
            <span className={cn(
              "absolute top-0.5 w-4 h-4 rounded-full transition-all",
              draftActive ? "left-[18px] bg-emerald-400" : "left-0.5 bg-rose-400"
            )} />
          </button>
        ) : (
          <span className={cn(
            "inline-block w-2 h-2 rounded-full",
            u.isActive ? "bg-emerald-400" : "bg-rose-400"
          )} title={u.isActive ? "Active" : "Inactive"} />
        )}
      </td>
      <td className="px-3 py-2.5 text-[10px] font-mono text-muted-foreground/60">
        {formatDateTime(u.createdAt)}
      </td>
      <td className="px-3 py-2.5 text-[10px] font-mono text-muted-foreground/60">
        {u.lastLoginAt ? formatDateTime(u.lastLoginAt) : "—"}
      </td>
      <td className="px-4 py-2.5 text-right">
        {!isAdmin ? (
          <span className="text-[9px] font-mono text-muted-foreground/30">read-only</span>
        ) : isEditing ? (
          <div className="flex items-center justify-end gap-1.5">
            <button
              onClick={onCancelEdit}
              className="text-[9px] uppercase tracking-widest text-muted-foreground hover:text-foreground"
            >
              cancel
            </button>
            <button
              onClick={() => saveM.mutate()}
              disabled={saveM.isPending}
              className="px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 disabled:opacity-40"
            >
              {saveM.isPending ? "Saving…" : "Save"}
            </button>
          </div>
        ) : (
          <div className="flex items-center justify-end gap-1">
            <button
              onClick={onStartEdit}
              className="px-2 py-0.5 rounded text-[9px] uppercase tracking-widest text-muted-foreground hover:text-foreground hover:bg-white/[0.05] transition-colors"
            >
              Edit
            </button>
            <button
              onClick={onResetPassword}
              className="px-2 py-0.5 rounded text-[9px] uppercase tracking-widest text-muted-foreground hover:text-amber-400 hover:bg-amber-500/10 transition-colors"
            >
              Reset PW
            </button>
            <button
              onClick={onDelete}
              className="px-2 py-0.5 rounded text-[9px] uppercase tracking-widest text-muted-foreground hover:text-rose-400 hover:bg-rose-500/10 transition-colors"
            >
              Delete
            </button>
          </div>
        )}
      </td>
    </tr>
  );
}

function ResetPasswordModal({
  userId,
  username,
  onClose,
}: {
  userId: string;
  username: string;
  onClose: () => void;
}) {
  const [pw, setPw] = useState("");
  const [requireChange, setRequireChange] = useState(true);

  const m = useMutation({
    mutationFn: () => usersApi.resetPassword(userId, pw, requireChange),
    onSuccess: () => {
      pushNotification({ type: "success", title: `Password reset for ${username}`, body: "New password is active." });
      onClose();
    },
    onError: handleErr,
  });

  return (
    <div
      role="dialog"
      aria-modal="true"
      onClick={onClose}
      className="fixed inset-0 z-[200] flex items-center justify-center p-6 bg-black/60 backdrop-blur-sm"
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className="w-full max-w-sm rounded-xl border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl p-5 space-y-3"
      >
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/80">
          Reset password
        </p>
        <p className="font-serif text-[18px] tracking-tight">{username}</p>
        <input
          type="password"
          value={pw}
          onChange={(e) => setPw(e.target.value)}
          placeholder="New password (≥ 6)"
          autoFocus
          className="w-full bg-[#070A0F] border border-white/10 rounded px-3 py-2 text-[12px] font-mono"
        />
        <label className="flex items-center gap-2 text-[11px] text-muted-foreground/80">
          <input
            type="checkbox"
            checked={requireChange}
            onChange={(e) => setRequireChange(e.target.checked)}
          />
          Require user to change password on next login
        </label>
        <div className="flex items-center justify-end gap-2 pt-2">
          <button
            onClick={onClose}
            className="text-[10px] uppercase tracking-widest text-muted-foreground hover:text-foreground"
          >
            Cancel
          </button>
          <button
            onClick={() => m.mutate()}
            disabled={pw.length < 6 || m.isPending}
            className="px-3 py-1.5 rounded text-[10px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors disabled:opacity-40"
          >
            {m.isPending ? "Saving…" : "Reset"}
          </button>
        </div>
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/50 mb-0.5 block">
        {label}
      </span>
      {children}
    </label>
  );
}

function handleErr(err: unknown) {
  if (err instanceof ApiError) {
    pushNotification({
      type: "error",
      title: `${err.status}`,
      body: err.body || err.message,
    });
  } else {
    pushNotification({
      type: "error",
      title: "Request failed",
      body: err instanceof Error ? err.message : "unknown error",
    });
  }
}
