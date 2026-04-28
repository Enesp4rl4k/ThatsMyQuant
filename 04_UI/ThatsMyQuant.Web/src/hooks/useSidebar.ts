import { createContext, useContext } from "react";

interface SidebarState {
  mobileOpen: boolean;
  toggle: () => void;
  close: () => void;
  open: () => void;
}

export const SidebarContext = createContext<SidebarState>({
  mobileOpen: false,
  toggle: () => {},
  close: () => {},
  open: () => {},
});

export const useSidebar = () => useContext(SidebarContext);
