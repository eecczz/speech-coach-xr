export type AvatarStyle = 'default' | 'date-suit';

const REGISTRY: Record<AvatarStyle, string> = {
  default: '/avatars/default.vrm',
  'date-suit': '/avatars/date-suit.vrm',
};

export function resolveAvatarUrl(style?: string | null): string {
  const key = (style ?? 'default') as AvatarStyle;
  return REGISTRY[key] ?? REGISTRY.default;
}
