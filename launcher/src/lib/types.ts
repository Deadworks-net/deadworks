export interface ModInfo {
  name: string;
  type: string;
  version: string;
}

export interface PlayerInfo {
  name: string;
  hero: string;
  team: number;
  kills: number;
  deaths: number;
  assists: number;
  level: number;
}

export type ContentKind = 'map' | 'addon';

export interface ContentManifestItem {
  filename: string;
  kind: ContentKind;
  version: number;
  compressed_size: number;
  download_url: string;
}

export interface Server {
  id: string;
  name: string;
  address: string;
  raw_address: string;
  country: string;
  online: boolean;
  player_count: number;
  max_players: number;
  map: string;
  players: PlayerInfo[];
  mods: ModInfo[];
  content_addons: string[];
  extra_maps: string[];
  content?: ContentManifestItem[];
  last_heartbeat: string | null;
}

export interface DownloadProgress {
  name: string;
  status: string;
  bytes_downloaded: number;
  total_bytes: number;
  item_index: number;
  total_items: number;
}

export interface BootstrapStatus {
  installed_version: number;
  pending_version: number;
  latest_version: number;
  min_version: number;
  /** An update is downloaded and only a game restart is standing in its way. */
  restart_required: boolean;
}

export interface ConnectResult {
  success: boolean;
  method: string;
  message: string;
}
