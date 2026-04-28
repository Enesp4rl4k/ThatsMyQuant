import { api } from "./client";

export type UserRole = "Admin" | "Operator" | "ReadOnly";

export interface OperatorUser {
  id: string;
  username: string;
  email: string;
  role: UserRole;
  isActive: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

export interface CreateUserRequest {
  username: string;
  email: string;
  password: string;
  role: UserRole;
}

export interface UpdateUserRequest {
  username?: string;
  email?: string;
  role?: UserRole;
  isActive?: boolean;
}

export const usersApi = {
  list: () => api.get<OperatorUser[]>("/api/auth/users"),
  create: (req: CreateUserRequest) => api.post<OperatorUser>("/api/auth/users", req),
  update: (id: string, req: UpdateUserRequest) =>
    api.patch<OperatorUser>(`/api/auth/users/${id}`, req),
  resetPassword: (id: string, newPassword: string, requireChangeOnLogin = false) =>
    api.post<{ ok: boolean }>(`/api/auth/users/${id}/password`, {
      newPassword,
      requireChangeOnLogin,
    }),
  remove: (id: string) => api.delete<void>(`/api/auth/users/${id}`),
};
