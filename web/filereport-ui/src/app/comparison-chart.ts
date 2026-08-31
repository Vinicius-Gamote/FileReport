import {
  AfterViewInit,
  Component,
  ElementRef,
  Input,
  OnChanges,
  OnDestroy,
  ViewChild,
} from '@angular/core';
import Chart from 'chart.js/auto';
import { Counts } from './models';
@Component({
  selector: 'app-comparison-chart',
  template:
    '<canvas #canvas role="img" aria-label="Comparison distribution; exact counts are in the adjacent table"></canvas>',
  styles: [':host { display:block; max-height:240px; }'],
})
export class ComparisonChart implements AfterViewInit, OnChanges, OnDestroy {
  @Input({ required: true }) counts!: Counts;
  @ViewChild('canvas') canvas!: ElementRef<HTMLCanvasElement>;
  private chart: Chart | undefined;
  ngAfterViewInit(): void {
    this.chart = new Chart(this.canvas.nativeElement, {
      type: 'doughnut',
      data: {
        labels: ['Added', 'Removed', 'Changed', 'Unchanged'],
        datasets: [
          {
            data: [],
            backgroundColor: ['#287d66', '#c06b55', '#d7a440', '#c6d8cf'],
            borderWidth: 0,
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: { legend: { position: 'bottom' }, tooltip: { enabled: false } },
      },
    });
    this.update();
  }
  ngOnChanges(): void {
    this.update();
  }
  private update(): void {
    if (!this.chart) return;
    const values = [
      this.counts.added,
      this.counts.removed,
      this.counts.changed,
      this.counts.unchanged,
    ].map(BigInt);
    const total = values.reduce((a, b) => a + b, 0n);
    this.chart.data.datasets[0].data = values.map((v) =>
      total ? Number((v * 1000000n) / total) / 10000 : 0,
    );
    this.chart.update();
  }
  ngOnDestroy(): void {
    this.chart?.destroy();
  }
}
