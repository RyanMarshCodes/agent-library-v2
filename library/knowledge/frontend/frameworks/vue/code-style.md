# Vue Code Style

Based on Vue.js official documentation and community best practices.

---

## Naming

### File Names

- Use PascalCase for components: `UserProfile.vue`
- Use kebab-case for file names generally
- Tests: `UserProfile.spec.ts` or `UserProfile.test.ts`

### Components

- PascalCase: `UserProfile`
- Descriptive names reflecting UI purpose

### Composables

- camelCase with `use` prefix: `useUserData`

### Props/Events

- Props: camelCase in JS, kebab-case in templates
- Events: kebab-case

---

## Project Structure

- Feature-based organization:
  ```
  src/
  ├─ features/
  │ ├─ user-profile/
  │ │ ├─ components/
  │ │ ├─ composables/
  │ │ └─ types.ts
  ├─ components/
  ├─ composables/
  ├─ stores/
  ```
- Colocate tests with components

---

## Components

### `<script setup>` Syntax

```vue
<script setup lang="ts">
import { ref, computed } from 'vue';

interface Props {
  userId: string;
}

const props = defineProps<Props>();
const emit = defineEmits<{
  save: [data: UserData];
}>();

const user = ref<User | null>(null);
const fullName = computed(() => 
  user.value ? `${user.value.firstName} ${user.value.lastName}` : ''
);
</script>

<template>
  <div class="user-profile">
    <h1>{{ fullName }}</h1>
  </div>
</template>
```

### Props

```typescript
// With defaults
const props = withDefaults(defineProps<{
  title?: string;
  count?: number;
}>(), {
  title: 'Default',
  count: 0,
});
```

---

## Reactive State

### ref vs reactive

```typescript
// Primitives - use ref
const count = ref(0);
const name = ref('John');

// Objects - use ref (preferred)
const user = ref({ name: 'John', age: 30 });
```

### Computed

```typescript
const doubleCount = computed(() => count.value * 2);
```

### Watch

```typescript
watch(count, (newVal, oldVal) => {
  // handle change
});

watch(() => props.userId, (newId) => {
  // fetch user by newId
}, { immediate: true });
```

---

## Composables

```typescript
// composables/useUserData.ts
export function useUserData(userId: Ref<string>) {
  const user = ref<User | null>(null);
  const loading = ref(false);
  
  const fetchUser = async () => {
    loading.value = true;
    // fetch logic
    loading.value = false;
  };
  
  watch(userId, fetchUser, { immediate: true });
  
  return { user, loading, fetchUser };
}
```

---

## Pinia Store

```typescript
// stores/user.ts
import { defineStore } from 'pinia';

export const useUserStore = defineStore('user', () => {
  const user = ref<User | null>(null);
  
  async function login(credentials: Credentials) {
    user.value = await api.login(credentials);
  }
  
  return { user, login };
});
```

---

## Template Best Practices

- Always use `:key` with `v-for`
- Use `v-if` / `v-else` for conditional rendering
- Avoid complex expressions in templates
- Use `v-model` with `.trim` modifiers

---

## Styling

- Use scoped CSS by default
- Use CSS Modules for component isolation

---

## Testing

- Use Vue Test Utils
- Use Vitest for unit tests

```typescript
import { mount } from '@vue/test-utils';

describe('UserProfile', () => {
  it('renders user name', () => {
    const wrapper = mount(UserProfile, {
      props: { userId: '123' },
    });
    expect(wrapper.text()).toContain('John');
  });
});
```

---

## Best Practices

- Use Composition API with `<script setup>`
- Use TypeScript for type safety
- Keep components small and focused
- Use Pinia for global state
- Use Volar for IDE support
