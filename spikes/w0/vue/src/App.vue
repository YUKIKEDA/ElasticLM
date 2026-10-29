<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import Detail from './Detail.vue';
import List from './List.vue';
import { m } from './messages';

const hash = ref(location.hash);
const changed = () => (hash.value = location.hash);
onMounted(() => addEventListener('hashchange', changed));
onUnmounted(() => removeEventListener('hashchange', changed));
const id = computed(() => /^#\/licenses\/(.+)$/.exec(hash.value)?.[1]);
</script>

<template>
  <main>
    <h1>{{ m.title }}</h1>
    <Detail v-if="id" :key="id" :id="decodeURIComponent(id)" />
    <List v-else />
  </main>
</template>
