import { api } from "./client";
import type { StoredUser } from "@/lib/authStorage";

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  token: string;
  expiresUtc: string;
  user: StoredUser;
}

export interface AuthConfig {
  enabled: boolean;
}

export const authApi = {
  config: () => api.get<AuthConfig>("/api/auth/config"),
  login: (req: LoginRequest) => api.post<LoginResponse>("/api/auth/login", req),
  me: () => api.get<StoredUser>("/api/auth/me"),
};
