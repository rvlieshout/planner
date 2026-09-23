import { ApiError, projects as projectsApi } from '$lib/api';
import type { ProjectDto } from '$lib/api/types';
import { rankAt } from '$lib/rank';
import { workspace } from '$lib/workspace.svelte';
import { toasts } from '$components/toast.svelte';

/*
 * Placing a project among its team's others — a team lead's call, and the server holds it to that.
 *
 * Only the project that moves is written: it takes a rank key between the two it now sits between,
 * and every other keeps the key it has. Like a card's move it is shown first and told to the server
 * afterwards; the realtime echo confirms the order rather than rearranging it, and a refusal puts the
 * project back where it was.
 */
export async function arrangeProject(ordered: ProjectDto[], project: ProjectDto, index: number): Promise<void> {
  const from = ordered.findIndex((candidate) => candidate.id === project.id);
  const others = ordered.filter((candidate) => candidate.id !== project.id);
  const to = Math.max(0, Math.min(index, others.length));

  if (from < 0 || from === to) return;

  const rank = rankAt(
    others.map((candidate) => candidate.rank),
    to
  );

  const place = (value: ProjectDto) => {
    workspace.projects = workspace.projects.map((candidate) => (candidate.id === value.id ? value : candidate));
  };

  place({ ...project, rank });

  try {
    place(await projectsApi.update(project.id, { rank }));
  } catch (failure) {
    place(project);
    toasts.error(failure instanceof ApiError ? failure.message : 'Moving the project failed.');
  }
}
